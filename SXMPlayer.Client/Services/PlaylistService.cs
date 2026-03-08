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
public class PlaylistService
{
    private readonly ILogger<PlaylistService> _logger;

    private readonly Dictionary<string, DateTimeOffset?> _streamTimeMap = new();
    private readonly ConcurrentDictionary<string, SXMStream> _sxmStreams = new();
    private readonly SemaphoreSlim _playlistSemaphore = new(1, 1);

    private string? _cachedPlaylist;

    private static readonly Regex ExtRegex = new("#EXT-X-PROGRAM-DATE-TIME:(.*)", RegexOptions.Compiled);
    private static readonly Regex ExtInfRegex = new(@"#EXTINF:(?<duration>[^,]+)(,(?<title>.*))?", RegexOptions.Compiled);

    public PlaylistService(ILogger<PlaylistService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyDictionary<string, DateTimeOffset?> StreamTimeMap => _streamTimeMap;

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
        SXMListener? listener,
        string? alias,
        bool useCache,
        ChannelItemData? currentChannel,
        bool listenerIsPrimary,
        Func<string, Task> setCurrentChannel,
        Func<string, Task<string>> getProxyPlaylistUrl,
        Func<string, Task<HttpResponseMessage?>> getHttpResponse,
        Func<string, DateTimeOffset?, Task<(string artist, string title, string? id)?>> getNowPlaying,
        NowPlayingData? nowPlayingFallback)
    {
        if (channelId == currentId && !listenerIsPrimary)
        {
            if (_cachedPlaylist is null && currentChannel != null)
            {
                return await GetStreamPlaylistAsync(currentChannel.Entity.Id, currentId, listener, channelId, useCache, currentChannel, listenerIsPrimary, setCurrentChannel, getProxyPlaylistUrl, getHttpResponse, getNowPlaying, nowPlayingFallback);
            }

            if (_cachedPlaylist is null && currentChannel is null)
            {
                throw new InvalidOperationException("Cannot get current playlist - no channel selected");
            }

            return _cachedPlaylist;
        }

        if (channelId == currentId)
        {
            channelId = currentChannel?.Entity.Id ?? throw new InvalidOperationException("No current channel selected");
        }

        if (!await _playlistSemaphore.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("Timed out waiting for playlist semaphore. Another request might be taking too long.");
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (_cachedPlaylist is not null) return _cachedPlaylist;
            throw new TimeoutException("Could not acquire lock to refresh playlist.");
        }

        try
        {
            await setCurrentChannel(channelId);
            var url = await getProxyPlaylistUrl(channelId);
            var res = await getHttpResponse(url);
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
                        var info = await getNowPlaying(channelId, ts);
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
            return _cachedPlaylist;
        }
        finally
        {
            _playlistSemaphore.Release();
        }
    }

    public async Task<string> GetProxyPlaylistUrlAsync(
        string channelId,
        Func<string, Task<Streams>> tuneSource,
        Func<string, Task<HttpResponseMessage?>> getHttpResponse)
    {
        Streams stream = await tuneSource(channelId);
        var bandwidths = stream.Urls.First(s => s.IsPrimary).Url;
        var res = await getHttpResponse(bandwidths);
        var allLines = await res!.Content.ReadAsStringAsync();
        string[] lines = SplitLines(allLines);
        var topBandwidth = lines.First(l => l.Contains("m3u8"));
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
