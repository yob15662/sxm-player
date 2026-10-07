using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Handles fetching and rewriting HLS playlists, time mapping, and stream metadata cache.
/// </summary>
public class PlaylistService : IStreamTimeMap
{
    private readonly ILogger<PlaylistService> _logger;
    private readonly ICurrentChannelService _currentChannel;
    private readonly IStreamTuner _tuner;
    private readonly IStreamHttpClient _http;
    private readonly INowPlayingProvider _nowPlaying;
    private readonly PlayerState _playerState;

    private readonly Dictionary<string, DateTimeOffset?> _streamTimeMap = new();
    private readonly ConcurrentDictionary<string, SXMStream> _sxmStreams = new();
    private readonly SemaphoreSlim _playlistSemaphore = new(1, 1);

    private string? _cachedPlaylist;

    private static readonly Regex ExtRegex = new("#EXT-X-PROGRAM-DATE-TIME:(.*)", RegexOptions.Compiled);
    private static readonly Regex ExtInfRegex = new(@"#EXTINF:(?<duration>[^,]+)(,(?<title>.*))?", RegexOptions.Compiled);

    public PlaylistService(
        ILogger<PlaylistService> logger,
        ICurrentChannelService currentChannel,
        IStreamTuner tuner,
        IStreamHttpClient http,
        INowPlayingProvider nowPlaying,
        PlayerState playerState)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _currentChannel = currentChannel ?? throw new ArgumentNullException(nameof(currentChannel));
        _tuner = tuner ?? throw new ArgumentNullException(nameof(tuner));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _nowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        _playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
    }

    public IReadOnlyDictionary<string, DateTimeOffset?> StreamTimeMap => _streamTimeMap;

    /// <summary>
    /// Clears the cached playlist so the next fetch retrieves fresh data.
    /// Used when a stale playlist is detected (e.g. a segment returned 404).
    /// </summary>
    public void InvalidatePlaylistCache()
    {
        _cachedPlaylist = null;
        _logger.LogDebug("Playlist cache invalidated; next fetch will retrieve fresh data.");
    }

    public double? AverageSegmentDuration { get; private set; }

    public bool TryGetStream(string channelId, out SXMStream? stream)
    {
        var found = _sxmStreams.TryGetValue(channelId, out var s);
        stream = s;
        return found;
    }

    public static double? ReadExtInfDuration(string line)
    {
        var match = ExtInfRegex.Match(line);
        if (match.Success)
        {
            return double.Parse(match.Groups["duration"].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static readonly TimeZoneInfo estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public void ExtractTimeMap(IEnumerable<string> m3u8Content)
    {
        lock (_streamTimeMap)
        {
            _streamTimeMap.Clear();
            DateTimeOffset? dateTime = null;
            foreach (var line in m3u8Content)
            {
                var match = ExtRegex.Match(line);
                if (match.Success)
                {
                    var badUTC = match.Groups[1].Value;
                    string cleanDateString = badUTC.Replace("+00:00", "").TrimEnd('Z');
                    DateTime estTime = DateTime.Parse(cleanDateString);
                    // During time change, SiriusXM sends invalid timestamps
                    if (estZone.IsInvalidTime(estTime))
                    {
                        estTime = estTime.AddHours(1);
                    }
                    dateTime = TimeZoneInfo.ConvertTime(estTime, estZone).ToUniversalTime();
                }
                if (!line.StartsWith("#"))
                {
                    var segmentName = line.Split('/').Last();
                    if (!string.IsNullOrWhiteSpace(segmentName))
                    {
                        _streamTimeMap.TryAdd(segmentName, dateTime);
                        dateTime = null;
                    }
                    else
                    {
                        _logger.LogWarning($"invalid line - no segment found: '{line}'");
                    }
                }
            }
            if (_streamTimeMap.Count == 0)
            {
                _logger.LogWarning($"Cannot parse m3u8: '{string.Join(',', m3u8Content)}'");
            }
        }
    }

    public static string[] SplitLines(string? res)
    {
        if (string.IsNullOrEmpty(res)) return Array.Empty<string>();
        return res.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    // Redirect the EXT-X-KEY URI to our proxy endpoint
    public static string RedirectKeyIfFound(string line)
    {
        if (line.StartsWith("#EXT-X-KEY:METHOD=AES-128,URI="))
        {
            var matches = HlsEncryptionService.GuidPattern.Matches(line);
            if (matches.Count > 0)
            {
                string lastGuid = matches[matches.Count - 1].Value;
                return $"#EXT-X-KEY:METHOD=AES-128,URI=\"/key/{lastGuid}\"";
            }
            else
            {
                throw new InvalidCastException($"No GUID found: '{line}'");
            }
        }
        return line;
    }

    public async Task<string?> GetStreamPlaylistAsync(
        string channelId,
        string currentId,
        string? alias,
        bool useCache)
    {
        var start = DateTimeOffset.UtcNow;
        var currentChannel = await _currentChannel.GetCurrentChannelAsync();
        _logger.LogDebug("GetStreamPlaylistAsync start - channelId={ChannelId} currentId={CurrentId} alias={Alias} useCache={UseCache} hasCurrentChannel={HasCurrentChannel} hasCachedPlaylist={HasCachedPlaylist}",
            channelId,
            currentId,
            alias,
            useCache,
            currentChannel is not null,
            _cachedPlaylist is not null);

        if (channelId == currentId)
        {
            channelId = currentChannel?.Entity.Id ?? throw new InvalidOperationException("No current channel selected");
            _logger.LogDebug("Resolved current channel alias to {ChannelId}", channelId);
        }

        if (!await _playlistSemaphore.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("Timed out waiting for playlist semaphore. Another request might be taking too long.");
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (_cachedPlaylist is not null)
            {
                _logger.LogDebug("Semaphore timeout fallback: returning cached playlist.");
                return _cachedPlaylist;
            }
            throw new TimeoutException("Could not acquire lock to refresh playlist.");
        }

        try
        {
            var nowPlayingFallback = _nowPlaying.GetNowPlaying();
            await _currentChannel.SetCurrentChannelAsync(channelId);
            var url = await GetProxyPlaylistUrlAsync(channelId, useSecondary: _playerState.UseSecondaryStreamUrl);
            _logger.LogDebug("Fetching source playlist - channelId={ChannelId} url={Url}", channelId, url);

            var res = await _http.GetAsync(url);
            var allLines = await res!.Content.ReadAsStringAsync();
            string[] lines = SplitLines(allLines);
            ExtractTimeMap(lines);

            var uri = new Uri(url);
            var version = uri.Segments[^2];
            var relPath = $"/stream/{alias ?? channelId}/{version}";
            var newLines = new List<string>();
            double totalDuration = 0;
            int segmentCount = 0;
            double? pendingDuration = null;

            foreach (var l in lines)
            {
                if (l.StartsWith("#EXTINF:"))
                {
                    var dur = ReadExtInfDuration(l) ?? 0;
                    if (dur > 0)
                    {
                        totalDuration += dur;
                        segmentCount++;
                    }
                    pendingDuration = dur;
                    continue;
                }

                if (l.Trim().EndsWith(".aac"))
                {
                    var segmentName = l.Split('/').Last();
                    string title = string.Empty;

                    if (StreamTimeMap.TryGetValue(segmentName, out var ts) && ts is not null)
                    {
                        var info = await _nowPlaying.GetNowPlaying(channelId, ts);
                        if (info is not null)
                        {
                            if (info.Value.id is null)
                            {
                                _logger.LogWarning($"No track ID for now playing - segment={segmentName} ts={ts?.ToLocalTime()}");
                            }
                            title = $"{info.Value.artist} - {info.Value.title}";
                        }
                    }

                    if (string.IsNullOrWhiteSpace(title) && nowPlayingFallback is not null)
                    {
                        title = $"{nowPlayingFallback.artist} - {nowPlayingFallback.song}";
                    }

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = "- - -";
                    }

                    var durStr = (pendingDuration ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
                    newLines.Add($"#EXTINF:{durStr},{title}");
                    newLines.Add($"{relPath}{l}");
                    pendingDuration = null;
                    continue;
                }

                newLines.Add(RedirectKeyIfFound(l));
            }

            AverageSegmentDuration = segmentCount == 0 ? null : (totalDuration / segmentCount);
            newLines.RemoveAll(l => l.Contains("EXT-X-ENDLIST"));
            newLines = newLines.Select(l => l.Replace("VOD", "EVENT")).ToList();

            _cachedPlaylist = string.Join('\n', newLines);
            _logger.LogDebug("GetStreamPlaylistAsync completed - channelId={ChannelId} sourceLineCount={SourceLineCount} outputLineCount={OutputLineCount} segmentCount={SegmentCount} avgSegmentDuration={AvgSegmentDuration} elapsedMs={ElapsedMs:F2}",
                channelId,
                lines.Length,
                newLines.Count,
                segmentCount,
                AverageSegmentDuration,
                (DateTimeOffset.UtcNow - start).TotalMilliseconds);
            return _cachedPlaylist;
        }
        finally
        {
            _playlistSemaphore.Release();
        }
    }

    /// <summary>
    /// A single variant stream from a master (multivariant) HLS playlist: the parsed
    /// <c>#EXT-X-STREAM-INF</c> attributes paired with the URI line that follows it.
    /// </summary>
    /// <param name="Uri">The variant playlist URI (relative or absolute, as written).</param>
    /// <param name="Bandwidth">The peak <c>BANDWIDTH</c> in bits per second, or <c>null</c> if not advertised.</param>
    /// <param name="AverageBandwidth">The <c>AVERAGE-BANDWIDTH</c> in bits per second, or <c>null</c> if not advertised.</param>
    /// <param name="Codecs">The <c>CODECS</c> value, or <c>null</c> if not advertised.</param>
    public readonly record struct HlsVariant(string Uri, long? Bandwidth, long? AverageBandwidth, string? Codecs);

    /// <summary>
    /// Parses a master (multivariant) HLS playlist into its variant streams. Each
    /// <c>#EXT-X-STREAM-INF</c> tag is paired with the next non-comment, non-empty line, which
    /// holds the variant URI. Variants appear in document order; the order of the tags does not
    /// affect parsing.
    /// </summary>
    public static HlsVariant[] ParseMasterPlaylist(IReadOnlyList<string> lines)
    {
        var variants = new List<HlsVariant>();

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                continue;
            }

            // The variant URI is the next non-comment, non-empty line.
            string? uri = null;
            for (int j = i + 1; j < lines.Count; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate.StartsWith('#'))
                {
                    continue;
                }
                uri = candidate;
                break;
            }

            if (uri is null)
            {
                continue;
            }

            variants.Add(new HlsVariant(
                Uri: uri,
                Bandwidth: ReadLongAttribute(line, "BANDWIDTH"),
                AverageBandwidth: ReadLongAttribute(line, "AVERAGE-BANDWIDTH"),
                Codecs: ReadStringAttribute(line, "CODECS")));
        }

        return variants.ToArray();
    }

    private static long? ReadLongAttribute(string line, string attribute)
    {
        var match = Regex.Match(line, $@"(?:^|[,:]){Regex.Escape(attribute)}=(?<v>\d+)");
        return match.Success && long.TryParse(match.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? ReadStringAttribute(string line, string attribute)
    {
        var match = Regex.Match(line, $@"(?:^|[,:]){Regex.Escape(attribute)}=""(?<v>[^""]*)""");
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>
    /// Selects the variant URI with the highest advertised bandwidth from a master playlist, so
    /// the order in which variants appear does not matter. Falls back to the first parsed variant
    /// when no <c>BANDWIDTH</c> is advertised, and to the first URI-looking line when the playlist
    /// has no <c>#EXT-X-STREAM-INF</c> entries at all.
    /// </summary>
    public static string SelectHighestBandwidthVariant(IReadOnlyList<string> lines)
    {
        var variants = ParseMasterPlaylist(lines);

        if (variants.Length > 0)
        {
            var best = variants
                .OrderByDescending(v => v.Bandwidth ?? -1)
                .First();
            return best.Uri;
        }

        // No EXT-X-STREAM-INF entries found; fall back to the first URI-looking line.
        return lines.First(l => l.Contains("m3u8"));
    }

    /// <summary>
    /// Resolves the source master playlist URL and selects the highest-bandwidth variant.
    /// </summary>
    /// <param name="useSecondary">
    /// When <c>true</c>, prefers the secondary (non-primary) stream URL, falling back to the
    /// primary URL if no secondary is available. When <c>false</c> (default), prefers the
    /// primary URL, falling back to any available URL.
    /// </param>
    public async Task<string> GetProxyPlaylistUrlAsync(
        string channelId,
        bool useSecondary = false)
    {
        Streams stream = await _tuner.TuneSourceAsync(channelId);
        var urls = stream.Urls ?? throw new InvalidOperationException($"No stream URLs returned for channel {channelId}");

        var selectedUrl = useSecondary
            ? (urls.FirstOrDefault(s => !s.IsPrimary) ?? urls.FirstOrDefault(s => s.IsPrimary) ?? urls.FirstOrDefault())
            : (urls.FirstOrDefault(s => s.IsPrimary) ?? urls.FirstOrDefault());

        if (selectedUrl is null)
        {
            throw new InvalidOperationException($"No {(useSecondary ? "secondary" : "primary")} stream URL available for channel {channelId}");
        }

        var bandwidths = selectedUrl.Url;
        var res = await _http.GetAsync(bandwidths);
        var allLines = await res!.Content.ReadAsStringAsync();
        string[] lines = SplitLines(allLines);
        var topBandwidth = SelectHighestBandwidthVariant(lines);
        var uri = new Uri(bandwidths);
        var tgtPath = string.Join("", uri.Segments[0..^1]);
        var finalM3U8 = $"{uri.Scheme}://{uri.Host}{tgtPath}{topBandwidth}";

        var basePath = $"{uri.Scheme}://{uri.Host}{tgtPath}";
        _sxmStreams[channelId] = new SXMStream(
            token: uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : "v1",
            channel: channelId,
            stream: uri.Segments.Length > 3 ? uri.Segments[3].Trim('/') : "sec-1",
            data: uri.Segments.Length > 4 ? uri.Segments[4].Trim('/') : "AAC_Data",
            path: basePath
        );

        return finalM3U8;
    }
}
