using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileSystemGlobbing.Internal;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using Polly;
using Polly.Extensions.Http;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SXMPlayer;

public record SXMListener(IPAddress IPAddress)
{
    public DateTimeOffset LastActivity { get; set; }
    public DateTimeOffset? LastPlaylistRequest { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; }
}

public partial record EntityData
{
    public string? ChannelName => (Texts?.Title?.Default is null) ? null : Texts!.Title!.Default;
    public string? ChannelDescription => (Texts?.Description?.Default is null) ? null : Texts!.Description!.Default;
    public string? Filename => (ChannelName is null) ? null : APITools.MakeFileName(ChannelName);
}

public partial record MetadataItem
{
    private static TimeZoneInfo edtZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    public DateTimeOffset StartTime => ParseBadUTC(this.Timestamp);

    private static DateTimeOffset ParseBadUTC(string ts)
    {
        string cleanDateString = ts.TrimEnd('Z');
        var parsed = DateTime.Parse(cleanDateString);
        // If you know it's actually EDT, convert it
        DateTimeOffset actualEdtTime = TimeZoneInfo.ConvertTime(parsed, edtZone);
        return actualEdtTime.ToUniversalTime();
    }
    public DateTimeOffset EndTime => StartTime.AddMilliseconds(this.Duration);
}

public class SiriusXMPlayer : IDisposable
{

    public const int MAX_AUTH_ATTEMPTS = 2;

    public const string MQTT_ARTIST = $"{MQTT_TOPIC}/Artist";

    public const string MQTT_CHANNEL = $"{MQTT_TOPIC}/Channel";

    public const string MQTT_TOPIC = "SiriusXM/NowPlaying";

    public const string MQTT_TRACK = $"{MQTT_TOPIC}/Track";

    private const int MAX_SEG_ENTRIES = 1000;

    private readonly string? mqttServer;

    private readonly string password;
    private readonly string? cacheFolder;
    private readonly APISession session;


    //private readonly HttpClientHandler session;
    private readonly string username;
    private List<ChannelItemData>? _allChannels;
    private DateTimeOffset? _audioOriginalTS;
    private ChannelItemData? _currentChannel;
    private DateTimeOffset? _currentSelectionTS;
    private DateTimeOffset? _lastNowPlayingListenersUpdate;
    private DateTimeOffset? _inactivityStart;
    private NowPlayingData? _nowPlaying;
    private Task _isActiveExpiryTimer;
    private Action<NowPlayingData> _nowPlayingListener;
    private HttpClientHandler _session;
    private string allCutsChannelInfo;
    private List<MetadataItem>? allCutsCurrentChannel;
    private string cachedPlaylist;
    private HttpClient httpClient;
    private DateTimeOffset? liveNowPlayingExpiry;
    private ILogger<SiriusXMPlayer> logger;
    private List<SXMListener> clients = new List<SXMListener>();
    private readonly object _allCutsLock = new object();

    private CancellationTokenSource tokenSource = new CancellationTokenSource();
    private CancellationTokenSource playlistRefreshSource = new CancellationTokenSource();
    private CacheManager cacheManager;
    private SemaphoreSlim playlistSemaphore = new SemaphoreSlim(1, 1);


    // New services
    private readonly HlsEncryptionService encryptionService;
    private readonly PlaylistService playlistService;
    private readonly IcecastStreamer icecastStreamer;

    //status - every 50 seconds
    //https://api.edge-gateway.siriusxm.com/playback/stream-enforcement/v1/status

    public SiriusXMPlayer(IConfiguration configuration,
                    ILogger<SiriusXMPlayer> logger,
                    ILoggerFactory loggerFactory,
                    IWebHostEnvironment hostingEnvironment)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        if (loggerFactory is null)
        {
            throw new ArgumentNullException(nameof(loggerFactory));
        }

        if (hostingEnvironment is null)
        {
            throw new ArgumentNullException(nameof(hostingEnvironment));
        }

        username = configuration.GetSection("SXM")["username"] ?? throw new InvalidProgramException("username is missing");
        password = configuration.GetSection("SXM")["password"] ?? throw new InvalidProgramException("password is missing");
        cacheFolder = configuration.GetSection("SXM")["cacheFolder"];
        if (cacheFolder != null)
            cacheManager = new CacheManager(cacheFolder, loggerFactory);
        InitializeActivityTimer();
        mqttServer = configuration.GetSection("MQTT")["Server"];
        if (mqttServer != null)
            logger.LogInformation($"Using MQTT server {mqttServer}");
        this.logger = logger;
        var contentRootPath = hostingEnvironment.ContentRootPath;
        currentChannelFile = Path.Combine(contentRootPath, "currentChannel.json");
        session = new APISession("https://api.edge-gateway.siriusxm.com", loggerFactory, contentRootPath, username, password);

        // init services
        encryptionService = new HlsEncryptionService(session, logger);
        playlistService = new PlaylistService(session, logger);
        // pass a provider for now-playing into IcecastStreamer
        icecastStreamer = new IcecastStreamer(logger, this);
    }

    public void Dispose()
    {
        _session?.Dispose();
        tokenSource.Cancel();
        _isActiveExpiryTimer?.Dispose();
        httpClient?.Dispose();
    }

    private async Task LoginIfNecessary(string reason)
    {
        if (_inactivityStart.HasValue && DateTimeOffset.Now - _inactivityStart > TimeSpan.FromHours(2))
        {
            logger.LogInformation($"Re-logging in due to inactivity - source:{reason}");
            await session.ReLogin();
            InitializeActivityTimer();
            _inactivityStart = null;
        }
        else
        {
            var doLogin = await session.LoginIfNecessary();
            if (doLogin)
            {
                InitializeActivityTimer();
            }
        }
    }

    public async Task<List<ChannelItemData>> GetChannelsAsync()
    {
        if (_allChannels != null) return _allChannels;
        await LoginIfNecessary(nameof(GetChannelsAsync));
        var container = await session.apiClient.AllChannelsAsync("3JoBfOCIwo6FmTpzM1S2H7", "false", "curated-grouping",
                                        "403ab6a5-d3c9-4c2a-a722-a94a6a5fd056", "0", "1000", "small_list", session.GetKey());
        _allChannels = container?.Container?.Sets?.First()?.Items?.ToList();
        if (_allChannels is null)
        {
            logger.LogError("No channels found");
            return new List<ChannelItemData>();
        }
        return _allChannels;
    }

    public async Task<IEnumerable<string>?> GetFavoritesAsync()
    {
        await LoginIfNecessary(nameof(GetFavoritesAsync));
        var favoriteData = await session.apiClient.GetLibraryAsync(session.GetKey(), CancellationToken.None);
        var favorites = favoriteData.AllDataMap;
        return favorites?.Select(m => m.Key);
    }

    public virtual NowPlayingData? GetNowPlaying() => _nowPlaying;

    public const string CURRENT_ID = "current";

    private string currentChannelFile;
    private Task<Task> statusTimer;

    private async Task<ChannelItemData?> GetCurrentChannel()
    {
        if (_currentChannel == null)
        {
            _currentChannel = await Tools.ReadJsonFile<ChannelItemData>(currentChannelFile, logger);
        }
        return _currentChannel;
    }

    private static Regex EXTINF_RegEx = new Regex(@"#EXTINF:(?<duration>[^,]+)(,(?<title>.*))?", RegexOptions.Compiled);

    private static double? ReadExtInfDuration(string line)
    {
        var match = EXTINF_RegEx.Match(line);
        if (match.Success)
        {
            return double.Parse(match.Groups["duration"].Value);
        }
        return null;
    }

    //{"actions":[
    //  {"progress":
    //  {"source":
    //      {"type":"channel-xtra","id":"62b1ccac-805b-28f2-a2a3-8030b5ecf55e"},
    //      "item":{"type":"xtra-channel-track","id":"376664ea-698c-87cb-4f52-bb6f728f843b"},
    //      "position":{"elapsedTimeMs":116541},
    //      "sourceTimestamp":"2024-12-26T23:09:29.163Z",
    //      "logicalClock":{"counter":193,"epoch":0}}}]}
    private int logicalClockCounter = 0;
    private DateTimeOffset? startTs;

    /// <summary>
    /// Send a progress action to the server
    /// </summary>
    /// <returns>true if progress has started</returns>
    public async Task<bool> SendProgressAction()
    {
        if (_currentChannel is null || _nowPlaying is null || !HasActiveClient())
        {
            return false;
        }

        var isChannelExtra = _currentChannel.Entity.Type != "channel-linear";
        var type = isChannelExtra ? "channel-xtra" : "channel-linear";
        var itemType = isChannelExtra ? "xtra-channel-track" : "cut-linear";
        Position position;
        Position2 position2;
        actions action;
        var hasStarted = false;
        if (isChannelExtra)
        {
            position = new Position() { ElapsedTimeMs = (startTs is null) ? 0 : (DateTimeOffset.Now - startTs.Value).TotalMilliseconds };
            position2 = new Position2() { ElapsedTimeMs = position.ElapsedTimeMs };
        }
        else
        {
            position = new Position() { AbsoluteTime = _audioOriginalTS!.Value.ToString("o") };
            position2 = new Position2() { AbsoluteTime = position.AbsoluteTime };
        }
        if (_channelHasChanged)
        {
            if (_currentChannel.Entity.Id is null || _nowPlaying.id is null)
            {
                logger.LogWarning("Cannot send progress action - no current channel or no nowPlaying data");
                return false;
            }
            _channelHasChanged = false;
            startTs = DateTimeOffset.UtcNow;
            action = new()
            {
                Start = new()
                {
                    Source = new() { Type = type, Id = _currentChannel.Entity.Id },
                    Item = new() { Type = itemType, Id = _nowPlaying.id },
                    Position = position2,
                    SourceTimestamp = DateTimeOffset.UtcNow.ToString("o"),
                    LogicalClock = new() { Counter = logicalClockCounter, Epoch = 0 }
                }
            };
            hasStarted = true;
        }
        else
        {
            action = new()
            {

                Progress = new()
                {
                    Source = new() { Type = type, Id = _currentChannel.Entity.Id },
                    Item = new() { Type = itemType, Id = _nowPlaying.id },
                    Position = position,
                    SourceTimestamp = DateTimeOffset.UtcNow.ToString("o"),
                    LogicalClock = new() { Counter = logicalClockCounter, Epoch = 0 }
                }
            };
        }
        logicalClockCounter++;
        logger.LogDebug($"Sending progress action - {action}");
        var result = await session.apiClient.ActionsAsync(new() { Actions = [action] });
        if (result?.FailedActions?.Count() > 0)
        {
            // concatenate failed action ids
            var failedIds = string.Join(", ", result.FailedActions.Select(o => o?.ToString()));
            logger.LogWarning($"Failed to send progress action - {failedIds} failed");
        }
        return hasStarted;
    }

    public virtual async Task<string?> GetStreamPlaylist(string channelId, SXMListener? listener, string? alias = null, bool useCache = true, int retries = 0)
    {
        await LoginIfNecessary(nameof(GetStreamPlaylist));
        StartStatusTimer();
        var currentChannel = await GetCurrentChannel();
        var isChannelChange = channelId != CURRENT_ID && channelId != currentChannel?.Entity.Id;
        if (isChannelChange)
        {
            logger.LogInformation($"Changing channel to {channelId} - client is {listener?.IPAddress}");
            SetPrimaryClient(listener);
        }
        else
        {
            EnsurePrimaryExists(listener);
        }
        if (listener is not null)
        {
            listener.LastPlaylistRequest = DateTimeOffset.Now;
        }
        StartProgressTimer(isChannelChange);
        // check if cache should be used
        if (channelId == CURRENT_ID && (!(listener?.IsPrimary ?? false)))
        {
            if (cachedPlaylist is null && currentChannel != null)
            {
                return await GetStreamPlaylist(currentChannel.Entity.Id, listener, channelId, useCache);
            }
            else
            if (cachedPlaylist is null && currentChannel is null)
            {
                throw new InvalidOperationException("Cannot get current playlist - no channel selected");
            }
            return cachedPlaylist;
        }
        if (channelId == CURRENT_ID)
        {
            channelId = currentChannel?.Entity.Id ?? throw new InvalidOperationException("No current channel selected");
        }

        if (!await playlistSemaphore.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            logger.LogWarning("Timed out waiting for playlist semaphore. Another request might be taking too long.");
            // Optional: wait a bit and return the cached playlist if available
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (cachedPlaylist is not null) return cachedPlaylist;
            // Or throw to indicate failure
            throw new TimeoutException("Could not acquire lock to refresh playlist.");
        }

        try
        {
            await SetCurrentChannel(channelId);
            var url = await GetProxyPlaylistURL(channelId);
            try
            {
                var res = await GetHttpResponseMessage(url, getQueryParameters());
                var allLines = await res.Content.ReadAsStringAsync();
                string[] lines = PlaylistService.SplitLines(allLines);
                playlistService.ExtractTimeMap(lines);
                var uri = new Uri(url);
                var tgtPath = string.Join("", uri.Segments[1..7]);
                var version = uri.Segments[^2];
                var relPath = $"/stream/{alias ?? channelId}/{version}";
                var newLines = new List<string>();
                double totalDuration = 0;
                int segmentCount = 0;

                double? pendingDuration = null;

                foreach (var l in lines)
                {
                    // Capture EXTINF duration but don't output yet; we'll emit it with a title just before the segment line
                    if (l.StartsWith("#EXTINF:"))
                    {
                        var dur = ReadExtInfDuration(l) ?? 0;
                        if (dur > 0)
                        {
                            totalDuration += dur;
                            segmentCount++;
                        }
                        pendingDuration = dur;
                        continue; // skip original EXTINF; we'll re-insert with title
                    }

                    if (l.Trim().EndsWith(".aac"))
                    {
                        // Compute title for this segment based on timestamp mapping or current now playing
                        var segmentName = l.Split('/').Last();
                        string title = string.Empty;

                        if (playlistService.StreamTimeMap.TryGetValue(segmentName, out var ts) && ts is not null)
                        {
                            var info = await GetNowPlaying(channelId, ts);
                            if (info is not null)
                            {
                                if (info.Value.id is null)
                                {
                                    logger.LogWarning($"No track ID for now playing - segment={segmentName} ts={ts?.ToLocalTime()}");
                                }
                                title = $"{info.Value.artist} - {info.Value.title}";
                            }
                        }

                        if (string.IsNullOrWhiteSpace(title) && _nowPlaying is not null)
                        {
                            title = $"{_nowPlaying.artist} - {_nowPlaying.song}";
                        }

                        // Fallback
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            title = "- - -";
                        }

                        var durStr = (pendingDuration ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
                        newLines.Add($"#EXTINF:{durStr},{title}");

                        // Then add the segment line rewritten to our proxy path
                        newLines.Add($"{relPath}{l}");

                        pendingDuration = null;
                        continue;
                    }

                    // Pass-through other lines (keys, headers, etc.)
                    newLines.Add(PlaylistService.RedirectKeyIfFound(l));
                }

                avgSegmentDuration = segmentCount == 0 ? (double?)null : (totalDuration / segmentCount);
                //remove EXT-X-ENDLIST from newLines
                newLines.RemoveAll(l => l.Contains("EXT-X-ENDLIST"));
                //replace #EXT-X-PLAYLIST-TYPE:VOD with #EXT-X-PLAYLIST-TYPE:EVENT at the same line
                newLines = newLines.Select(l => l.Replace("VOD", "EVENT")).ToList();

                cachedPlaylist = String.Join('\n', newLines);
                return cachedPlaylist;
            }
            catch (ApiException ex)
            {
                logger.LogWarning(ex, $"GetRealPlaylist data - Forbidden error - retrying - retries={retries}");
                await session.ReLogin();
                InitializeActivityTimer();
                return await GetStreamPlaylist(channelId, listener, useCache: false, retries: retries++);
            }
        }
        finally
        {
            playlistSemaphore.Release();
        }

    }

    private void EnsurePrimaryExists(SXMListener? client)
    {
        if (client != null)
        {
            lock (clients)
            {
                if (!clients.Any(c => c.IsPrimary))
                {
                    client.IsPrimary = true;
                }
            }
        }
    }

    private void SetPrimaryClient(SXMListener? client)
    {
        if (client != null)
        {
            lock (clients)
            {
                foreach (var c in clients)
                {
                    c.IsPrimary = c == client;
                }
            }
        }
    }

    private async Task<ChannelItemData> SetCurrentChannel(string channelId)
    {
        if (_currentChannel?.Entity.Id != channelId)
        {
            logger.LogInformation($"Setting current channel to {channelId}");
            _channelHasChanged = true;
            playlistRefreshSource.Cancel();
            playlistRefreshSource = new CancellationTokenSource();
            lock (_allCutsLock)
            {
                allCutsCurrentChannel = null;
            }
        }
        var allChannels = await GetChannelsAsync();
        _currentChannel = allChannels.FirstOrDefault(c => c.Entity.Id == channelId);
        await Tools.WriteObject(_currentChannel, currentChannelFile, logger);
        if (_currentChannel is null)
        {
            throw new InvalidOperationException($"Channel {channelId} not found");
        }
        return _currentChannel;
    }

    public async Task<Stream> GetSegment(string channelId, string version, string segmentId, SXMListener? client)
    {
        // Never inject ICY metadata into HLS AAC segments; this corrupts the container and leads to disconnects.
        return await GetSegmentInternal(channelId, version, segmentId, client);
    }

    private async Task<Stream> GetSegmentInternal(string channelId, string version, string segmentId, SXMListener? client)
    {
        UpdateClientActivity();
        if (channelId == CURRENT_ID && _currentChannel != null)
        {
            //set the real channel
            channelId = _currentChannel!.Entity.Id!;
        }
        // Channel mismatch check - don't allow segment requests to change channels
        // Only GetStreamPlaylist with explicit channel IDs should trigger SetCurrentChannel
        if (channelId != _currentChannel?.Entity.Id)
        {
            throw new InvalidOperationException($"Channel mismatch: requested {channelId} but current is {_currentChannel?.Entity.Id}. Channel must be set via playlist request first.");
        }

        if (!sxmStreams.TryGetValue(channelId, out var stream) || stream is null)
        {
            // Try to populate from current tuning info
            try
            {
                _ = await GetProxyPlaylistURL(channelId);
            }
            catch { }
            sxmStreams.TryGetValue(channelId, out stream);
        }
        if (stream is null)
        {
            throw new InvalidOperationException($"Cannot get segment {segmentId} for channel {segmentId} - stream info is not initialized");
        }

        // Always update now playing based on the requested segment, even if served from cache
        if (channelId != CURRENT_ID)
        {
            var segment = new SXMSegment(stream, segmentId);
            await SetNowPlayingFromSegment(segment);
        }

        if (cacheManager != null && (await cacheManager.GetCachedFile(segmentId)) is byte[] cached)
        {
            return new MemoryStream(cached);
        }

        //var ts = playlistMap?[v]
        //AAC_Data/{channel:regex(.*)}/{stream:regex(.*)}/{name:regex(.*\\.aac)}
        //var currentTrack = await GetNowPlaying(channel, ts);
        var url = $"{stream.path}{version}/{segmentId}";
        var parameters = getQueryParameters();
        var output = await GetHttpResponseMessage(url, parameters);
        // read output to byte array
        if (output != null)
        {
            var content = await output.Content.ReadAsByteArrayAsync();
            if (cacheManager != null)
                await cacheManager.SaveFile(segmentId, content, DateTimeOffset.Now.AddHours(1));
            return new MemoryStream(content);
        }
        return await output.Content.ReadAsStreamAsync();
    }

    //#EXT-X-KEY:METHOD=AES-128,URI="https://api.edge-gateway.siriusxm.com/playback/key/v1/00000000-0000-0000-0000-000000000000"

    public void RegisterNowPlayingListener(Action<NowPlayingData> listener)
    {
        _nowPlayingListener = listener;
    }

    private async Task SetNowPlayingFromSegment(SXMSegment segment, bool retry = true)
    {
        _audioOriginalTS = null;
        var channels = await GetChannelsAsync();
        var prevNowPlaying = _nowPlaying;
        _nowPlaying = null;
        if (playlistService.StreamTimeMap.TryGetValue(segment.segment, out var audioTS))
        {
            _audioOriginalTS = audioTS;
            var trackInfo = await GetNowPlaying(segment.stream.channel, _audioOriginalTS);
            var channelName = channels.FirstOrDefault(s => s.Entity.Id == segment.stream.channel);
            if (trackInfo != null && channelName != null)
            {
                _nowPlaying = new NowPlayingData(channelName.Entity.ChannelName, trackInfo.Value.artist, trackInfo.Value.title, trackInfo.Value.id);
            }
            else if (channelName != null)
            {
                _nowPlaying = new NowPlayingData(channelName.Entity.ChannelName, "-", "-", null);
            }
        }
        else
        {
            if (retry)
            {   // try to refresh cuts and retry once
                await RefreshAllCuts(segment.stream.channel);
                await SetNowPlayingFromSegment(segment, false);
                return;
            }
            logger.LogWarning($"Cannot find {segment.segment} in playlistMap");
            _nowPlaying = new NowPlayingData("?", "?", "?", null);
        }
        if (_nowPlaying != prevNowPlaying || _lastNowPlayingListenersUpdate is null)
        {
            _nowPlayingListener?.Invoke(_nowPlaying!);
            if (mqttServer != null && _nowPlaying != null)
            {
                _lastNowPlayingListenersUpdate = DateTimeOffset.Now;
            }
        }
        if (_nowPlaying != null && _lastNowPlayingListenersUpdate < DateTimeOffset.Now.AddMinutes(-2))
        {
            _lastNowPlayingListenersUpdate = DateTimeOffset.Now;
        }
        _currentSelectionTS = DateTimeOffset.Now;
    }

    public virtual async Task<byte[]> GetDecryptionKey(string guid)
    {
        return await encryptionService.GetDecryptionKey(guid);
    }

    private static void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(Windows NT 10.0; Win64; x64)"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AppleWebKit", "537.36"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(KHTML, like Gecko)"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Chrome", "101.0.4911.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Safari", "537.36"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Edg", "101.0.1193.0"));
    }

    static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.NotFound)
            .WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2,
                                                                        retryAttempt)));
    }

    private static string[] SplitLines(string? res)
    {
        return res.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }




    private async Task<HttpResponseMessage?> GetHttpResponseMessage(string url, Dictionary<string, string> parameters)
    {
        var builder = new UriBuilder(url);
        builder.Query = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
        //var res = await GetRetryPolicy().ExecuteAsync(async () =>
        //{
        var request = new HttpRequestMessage() { RequestUri = builder.Uri, Method = HttpMethod.Get };
        ConfigureRequest(request);

        var response = await session.GetHttpClient().SendAsync(request);
        //});

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var responseData_ = response.Content == null ? null : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new ApiException("Unexpected error", (int)response.StatusCode, responseData_, null, null);
        }
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Received status code {response.StatusCode} for url \'{url}\'");
        }

        return response;
    }

    private PeriodicTimer progressTimer;
    private void StartProgressTimer(bool isChannelChanged)
    {
        if (progressTimerTask is not null)
        {
            if (isChannelChanged)
            {
                _channelHasChanged = true;
                progressTimer.Period = TimeSpan.FromSeconds(1);
            }
            return;
        }
        _channelHasChanged = true;
        progressTimerTask = Task.Factory.StartNew(
            async () =>
            {
                progressTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (!tokenSource.IsCancellationRequested)
                {
                    var hasStarted = await SendProgressAction();
                    if (hasStarted)
                    {
                        progressTimer.Period = TimeSpan.FromSeconds(60);
                    }
                    await progressTimer.WaitForNextTickAsync(tokenSource.Token);
                }
            });
    }

    private void StartStatusTimer()
    {
        if (statusTimer is not null)
        {
            return;
        }
        statusTimer = Task.Factory.StartNew(
            async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(50));
                while (!tokenSource.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(tokenSource.Token);
                    var statusResponse = await session.apiClient.StatusAsync();
                    logger.LogDebug($"Status response - state={statusResponse.State}");
                    if (statusResponse?.State?.ToLower() != "active")
                    {
                        logger.LogCritical($"Status is not active - state={statusResponse.State}");
                        tokenSource.Cancel();
                    }
                }
            });
    }

    private string FormatToISO8601(DateTimeOffset dateTime)
    {
        //var edtZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        //var edtTime = TimeZoneInfo.ConvertTime(dateTime, edtZone);
        //return edtTime.DateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return dateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    // Mon, 20 Oct 2025 19:58:58 GMT
    // channelId: "e6333906-2e89-6c07-59d6-36d09248b8dc"
    // endTimestamp: "2025-10-20T19:59:37.941Z"
    // startTimestamp: "2025-10-20T16:47:27.492Z"

    private async Task RefreshAllCuts(string channel)
    {
        //Wed, 18 Sep 2024 13:38:39 GMT
        var ts = DateTimeOffset.Now;
        var start = ts.AddMinutes(-10).AddHours(-3);
        var end = ts.AddMinutes(1);
        //endTimestamp:        "2024-09-18T13:39:19.000Z" ISO 8601
        //startTimestamp:        "2024-09-18T13:33:03.222Z"
        var liveUpdateData = await session.apiClient.LiveUpdateAsync(new()
        {
            ChannelId = channel,
            StartTimestamp = FormatToISO8601(start),
            EndTimestamp = FormatToISO8601(end)
        });
        lock (_allCutsLock)
        {
            allCutsChannelInfo = channel;
            allCutsCurrentChannel = liveUpdateData?.Items?.ToList();
        }
    }

    private async Task<(string artist, string title, string? id)?> GetNowPlaying(string channelId, DateTimeOffset? ts, bool tryRefresh = true)
    {
        List<MetadataItem>? currentCuts;
        lock (_allCutsLock)
        {
            currentCuts = allCutsCurrentChannel;
        }

        if (ts is null || currentCuts is null)
        {
            if (currentCuts is null && tryRefresh)
            {
                await RefreshAllCuts(channelId);
                cutsRefreshed++;
                return await GetNowPlaying(channelId, ts, false);
            }
            var channelInfo = (await GetChannelsAsync()).First(c => c.Entity.Id == channelId);
            logger.LogWarning($"no cuts or no current channel - channel={channelId} ts={ts}");
            return (channelInfo.Entity.ChannelName, channelInfo.Entity.Texts?.Description?.Default ?? "-", null);
        }

        var utcTime = ts.Value.ToUniversalTime();
        var current = currentCuts.FirstOrDefault(ct => ct.StartTime <= utcTime && ct.EndTime.AddSeconds(1) >= utcTime);
        if (current is null)
            current = currentCuts.FirstOrDefault(ct => ct.StartTime <= utcTime);
        if (current is null && tryRefresh)
        {
            logger.LogInformation($"Now playing not found in cuts - refreshing cuts - channel={channelId} ts={ts?.ToLocalTime()}");
            if (cutsRefreshed >= 2)
            {
                logger.LogWarning($"Too many cuts refreshes - giving up - channel={channelId} ts={ts?.ToLocalTime()}");
                var channelInfo = (await GetChannelsAsync()).First(c => c.Entity.Id == channelId);
                return (channelInfo.Entity.ChannelName, channelInfo.Entity.Texts?.Description?.Default ?? "-", null);
            }
            await RefreshAllCuts(channelId);
            cutsRefreshed++;
            return await GetNowPlaying(channelId, ts, false);
        }
        cutsRefreshed = 0;
        if (current is null && utcTime <= liveNowPlayingExpiry?.ToUniversalTime())
        {
            // not found in history => live now playing
            current = currentCuts.FirstOrDefault(ct => ct.StartTime == ct.EndTime);
        }
        if (current is null && (channelId == _currentChannel?.Entity.Id || _currentChannel is null))
        {
            // not found in history => live now playing
            current = currentCuts.FirstOrDefault(ct => ct.StartTime == ct.EndTime);
        }

        if (current is null)
        {
            // not found in history => live now playing
            current = currentCuts.FirstOrDefault(ct => ct.StartTime == ct.EndTime);
            if (current is not null && ts is not null)
            {
                liveNowPlayingExpiry = current?.EndTime.AddSeconds((ts.Value - current.EndTime).TotalSeconds + 30);
            }
        }
        else
        {
            // historical data            
            liveNowPlayingExpiry = null;
        }

        var lastCut = currentCuts.OrderByDescending(e => e.EndTime).FirstOrDefault();
        var firstCut = currentCuts.OrderBy(e => e.StartTime).FirstOrDefault();
        if (utcTime >= lastCut?.EndTime)
        {
            current = lastCut;
        }

        if (current != null)
        {
            return (current?.ArtistName ?? "-", current?.Name ?? "-", current?.Id);
        }
        else
        {
            var channelInfo = (await GetChannelsAsync()).First(c => c.Entity.Id == channelId);
            logger.LogWarning($"Invalid now playing data - channel={channelId} ts={ts?.ToLocalTime()} - first={firstCut?.StartTime.ToLocalTime()} - last={lastCut?.StartTime.ToLocalTime()}");
            return (channelInfo.Entity.ChannelName, channelInfo.Entity.Texts?.Description?.Default ?? "-", null);
        }
    }

    private ConcurrentDictionary<string, SXMStream> sxmStreams = new();
    private Task<Task> progressTimerTask;
    private bool _channelHasChanged;
    private double? avgSegmentDuration;
    private int cutsRefreshed;

    public int? ICYMetaInt { get; internal set; }
    public bool DisableICYMetadata { get; internal set; } = false;

    private async Task<string> GetProxyPlaylistURL(string channelId)
    {
        Streams stream1 = await TuneSource(channelId);
        var bandwidths = stream1.Urls.First(s => s.IsPrimary).Url;
        var res = await GetHttpResponseMessage(bandwidths, getQueryParameters());
        var allLines = await res.Content.ReadAsStringAsync();
        string[] lines = SplitLines(allLines);
        var topBandwidth = lines.First(l => l.Contains("m3u8"));
        var uri = new Uri(bandwidths);
        var tgtPath = string.Join("", uri.Segments[0..^1]);
        // Combine the base URL with the top bandwidth variant
        var finalM3U8 = $"{uri.Scheme}://{uri.Host}{tgtPath}{topBandwidth}";

        // Cache stream base path so we can resolve AAC segments later
        // Example source: /v1/<token>/sec-1/AAC_Data/<channel>/...
        var basePath = $"{uri.Scheme}://{uri.Host}{tgtPath}"; // ends with '/'
        sxmStreams[channelId] = new SXMStream(
            token: uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : "v1",
            channel: channelId,
            stream: uri.Segments.Length > 3 ? uri.Segments[3].Trim('/') : "sec-1",
            data: uri.Segments.Length > 4 ? uri.Segments[4].Trim('/') : "AAC_Data",
            path: basePath
        );

        return finalM3U8;
    }

    private async Task<Streams> TuneSource(string channelId, int retries = 0)
    {
        if (retries >= 5)
        {
            logger.LogError($"Too many retries");
            throw new InvalidOperationException("Too many retries");
        }
        await LoginIfNecessary(nameof(TuneSource));
        try
        {
            var allChannels = await GetChannelsAsync();
            var channel = allChannels.SingleOrDefault(c => c.Entity.Id == channelId);
            if (channel is null)
            {
                throw new InvalidOperationException($"Channel {channelId} not found - {allChannels.Count} channels loaded");
            }
            var manifestVariant = "FULL";
            if (channel.Entity.Type == "channel-linear")
            {
                manifestVariant = "WEB";
            }
            var tuneSource = await session.apiClient.TuneSourceAsync(new()
            {
                Id = channelId,
                HlsVersion = "V3",
                ManifestVariant = manifestVariant,
                MtcVersion = "V2",
                Type = channel.Entity.Type
            });
            var stream1 = tuneSource.Streams!.First();
            if (channel.Entity.Type == "channel-linear")
            {
                lock (_allCutsLock)
                {
                    allCutsCurrentChannel = stream1.Metadata?.Live?.Items!.ToList();
                }
                return stream1;
            }
            else
            {
                //var metadata = stream1.Metadata!.
                return stream1;
            }
        }
        catch (ApiException aex)
        {
            if (aex.StatusCode == 200)
            {
                logger.LogCritical(aex, $"Error loading cuts {aex.Message}");
                throw new InvalidOperationException("Error loading cuts");
            }
            else
            {
                logger.LogWarning($"Error loading cuts - error {aex.StatusCode}:{aex.Response ?? aex.Message} - retrying - retries={retries}");
                await Task.Delay(TimeSpan.FromSeconds(5 * (retries + 1)), tokenSource.Token);
                await session.ReLogin();
                InitializeActivityTimer();
                return await TuneSource(channelId, retries + 1);
            }
        }
    }

    private Dictionary<string, string> getQueryParameters() => new Dictionary<string, string> { };

    private void InitializeActivityTimer()
    {

        httpClient = null;
        _isActiveExpiryTimer ??= Task.Factory.StartNew(NowPlayingTimeoutHandler, tokenSource.Token)
            .ContinueWith(_ => { logger.LogError(_.Exception, "Error with timeout handler"); },
            TaskContinuationOptions.OnlyOnFaulted); ;
    }

    private async void NowPlayingTimeoutHandler()
    {
        while (!tokenSource.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), tokenSource.Token);
            if (!tokenSource.IsCancellationRequested)
            {
                UpdateClientActivity();
                if (_currentSelectionTS != null && DateTimeOffset.Now - _currentSelectionTS > TimeSpan.FromSeconds(120))
                {
                    _inactivityStart = DateTimeOffset.Now;
                    _nowPlaying = new NowPlayingData("-", "-", "-", null);
                    _nowPlayingListener?.Invoke(_nowPlaying);
                    logger.LogInformation($"Timeout detected for segments");
                    _currentSelectionTS = null;
                }
            }
        }
    }

    public EntityData? GetChannelFromFilename(string fileName)
    {
        return _allChannels?.First(c => c.Entity.Filename == fileName).Entity;
    }

    public SXMListener TrackListenerIP(IPAddress ipAddress)
    {
        UpdateClientActivity();
        //if (ipAddress is null)
        //    return null;
        lock (clients)
        {
            var client = clients.FirstOrDefault(c => c.IPAddress.Equals(ipAddress));
            var hasPrimary = clients.Any(c => c.IsPrimary);
            if (client != null)
            {
                client.LastActivity = DateTimeOffset.Now;
                client.IsActive = true;
                return client;
            }
            else
            {
                var newClient = new SXMListener(ipAddress) { LastActivity = DateTimeOffset.Now, IsPrimary = !hasPrimary, IsActive = true };
                logger.LogInformation($"Adding new client {newClient.IPAddress} - Primary: {newClient.IsPrimary}");
                clients.Add(newClient);
                return newClient;
            }
        }
    }

    public bool HasActiveClient()
    {
        lock (clients)
        {
            return clients.Any(c => c.IsActive);
        }
    }

    private void UpdateClientActivity()
    {
        lock (clients)
        {
            var timeoutSeconds = 60 * 2;
            var toDeactivate = clients.Where(c => c.IsActive && DateTimeOffset.Now - c.LastActivity > TimeSpan.FromSeconds(avgSegmentDuration * 5 ?? timeoutSeconds)).ToList();

            if (toDeactivate.Any())
            {
                // Cancel playlist producers for inactive clients
                icecastStreamer.CancelProducersForInactiveClients(toDeactivate);
            }

            foreach (var c in toDeactivate)
            {
                c.IsActive = false;
                c.IsPrimary = false;
            }

            var activeClientCount = clients.Count(c => c.IsActive);
            foreach (var c in toDeactivate)
            {
                logger.LogInformation($"Deactivating inactive client {c.IPAddress} - {activeClientCount} active clients");
            }

            if (clients.Any(c => c.IsActive) && !clients.Any(c => c.IsPrimary && c.IsActive))
            {
                var newPrimary = clients.First(c => c.IsActive);
                newPrimary.IsPrimary = true;
                logger.LogInformation($"Setting primary client to {newPrimary.IPAddress}");
            }
        }
    }

    public async Task<string?> GetCurrentChannelImage()
    {
        var current = await GetCurrentChannel();
        if (current == null)
        {
            return null;
        }
        var imgKey = current.Entity.Images?.Tile?.Aspect_1x1?.Default?.Url;
        if (imgKey == null)
        {
            return null;
        }
        var imgParams = $"{{\"key\":\"{imgKey}\",\"edits\":[{{\"format\":{{\"type\":\"jpeg\"}}}},{{\"resize\":{{\"width\":600,\"height\":600}}}}]}}";
        // base 64 encode
        var imgParamsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(imgParams));
        return $"https://imgsrv-sxm-prod-device.streaming.siriusxm.com/{imgParamsBase64}";
    }

    public const int ICY_META_BLOCK = 8162;

    /// <summary>
    /// Starts a continuous Icecast-compatible AAC stream from the HLS source with progressive ICY metadata.
    /// Decrypts AES-128 encrypted HLS segments when an EXT-X-KEY is in effect.
    /// </summary>
    public async Task StreamIcecastAsync(string channelId, HttpContext ctx, CancellationToken ct)
    {
        var listener = TrackListenerIP(ctx.Connection.RemoteIpAddress!);
        _ = await GetStreamPlaylist(channelId, listener, alias: CURRENT_ID, useCache: false);
        var current = await GetCurrentChannel();
        if (current == null)
        {
            throw new InvalidOperationException("No channel data available");
        }
        string realChannelId = channelId == CURRENT_ID ? current?.Entity.Id ?? throw new InvalidOperationException("No channel selected") : channelId;
        if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
        {
            await SetCurrentChannel(channelId);
        }


        // ICY headers only if requested by client (or forced for VLC user-agents), but never for MPD/libcurl
        bool injectMeta = ctx.Request.Headers.TryGetValue("Icy-MetaData", out var metaReq) && string.Equals(metaReq, "1", StringComparison.Ordinal);
        var userAgent = ctx.Request.Headers["User-Agent"].ToString();
        var ua = userAgent?.ToLowerInvariant() ?? string.Empty;
        // Force-enable for VLC which often omits the header for AAC
        //if (!injectMeta && ua.Contains("vlc"))
        //{
        //    injectMeta = true;
        //}
        int metaInt = injectMeta ? (ICYMetaInt ?? ICY_META_BLOCK) : int.MaxValue;

        if (injectMeta)
        {
            logger.LogInformation($"Injecting ICY metadata every {metaInt} bytes for client {ctx.Connection.RemoteIpAddress} (UA: {userAgent})");
            ctx.Response.Headers["icy-metaint"] = metaInt.ToString();
            var np = GetNowPlaying();
            if (np is not null)
            {
                ctx.Response.Headers["icy-name"] = $"Sirius XM - {current.Entity.ChannelName}";
            }
        }
        // Conservative headers for legacy clients
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
        ctx.Response.Headers["Accept-Ranges"] = "none";
        // Friendly hint for reverse proxies like nginx to avoid buffering
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        // Ensure Kestrel manages transfer framing (no Content-Length implies chunked for HTTP/1.1)
        ctx.Response.ContentLength = null;
        // Use audio/aacp when not injecting ICY; some clients fail on aacp without ICY
        ctx.Response.ContentType = injectMeta ? "audio/aacp" : "audio/aac";
        await ctx.Response.StartAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            // Force to resend metadata on next segment
            icecastStreamer.ClearMetadataState();
            // Producer-consumer setup
            var segmentQueue = System.Threading.Channels.Channel.CreateBounded<IcecastStreamer.SegmentWorkItem>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

            Func<string> channelIdProvider = () => (channelId == CURRENT_ID ? _currentChannel?.Entity.Id : realChannelId) ?? throw new InvalidOperationException("no channel available");

            var producerTask = icecastStreamer.StartPlaylistProducer(segmentQueue.Writer, GetCurrentChannel, listener, playlistRefreshSource.Token, ct);

            int bytesUntilMeta = metaInt;
            try
            {
                await foreach (var item in segmentQueue.Reader.ReadAllAsync(ct))
                {
                    // Data is already decrypted by the producer
                    if (item.AudioData is not null)
                    {
                        bytesUntilMeta = await icecastStreamer.WriteWithIcyAsync(item.AudioData.Value, ctx, injectMeta, metaInt, bytesUntilMeta, ct);
                        listener.LastActivity = DateTimeOffset.Now;
                    }
                    else
                    {
                        logger.LogWarning($"Received segment {item.SegmentName} with no data");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when client disconnects or playlist refreshes
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in Icecast streaming consumer.");
            }
            finally
            {
                await producerTask; // Ensure producer stops
            }
            if (playlistRefreshSource.IsCancellationRequested)
            {
                logger.LogInformation("Playlist refresh requested, restarting producer.");
                continue;
            }
        }
    }

}
