using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using System.Text;
using Polly;
using Polly.Retry;

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
        // During time change, SiriusXM sends invalid timestamps
        if (edtZone.IsInvalidTime(parsed))
        {
            parsed = parsed.AddHours(1);
        }
        DateTimeOffset actualEdtTime = TimeZoneInfo.ConvertTime(parsed, edtZone);
        return actualEdtTime.ToUniversalTime();
    }
    public DateTimeOffset EndTime => StartTime.AddMilliseconds(this.Duration);
}

public class SiriusXMPlayer : IDisposable
{

    public const int MAX_AUTH_ATTEMPTS = 2;

    public const int ICY_META_BLOCK = 8162;

    // MQTT constants removed - now in MetadataService

    private const int MAX_SEG_ENTRIES = 1000;

    private readonly string? mqttServer;

    private readonly string password;
    private readonly string? cacheFolder;
    private readonly APISession session;
    private readonly SxmSessionService sxmSessionService;


    //private readonly HttpClientHandler session;
    private readonly string username;
    private List<ChannelItemData>? _allChannels;
    // Metadata fields removed - now in MetadataService
    private ChannelItemData? _currentChannel;
    // Metadata fields removed - now in MetadataService
    private HttpClientHandler? _session;
    // Metadata fields removed - now in MetadataService
    private ILogger<SiriusXMPlayer> logger;
    private List<SXMListener> clients = new List<SXMListener>();
    // Metadata fields removed - now in MetadataService

    private CancellationTokenSource tokenSource = new CancellationTokenSource();
    private CancellationTokenSource playlistRefreshSource = new CancellationTokenSource();
    private CacheManager cacheManager = null!;


    // New services
    private readonly HlsEncryptionService encryptionService;
    private readonly PlaylistService playlistService;
    private readonly IcecastStreamer icecastStreamer;
    private readonly ResiliencePipeline<HttpResponseMessage> httpRetryPipeline;
    private readonly MetadataService metadataService;

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
        mqttServer = configuration.GetSection("MQTT")["Server"];
        if (mqttServer != null)
            logger.LogInformation($"Using MQTT server {mqttServer}");
        this.logger = logger;
        var contentRootPath = hostingEnvironment.ContentRootPath;
        currentChannelFile = Path.Combine(contentRootPath, "currentChannel.json");
        session = new APISession("https://api.edge-gateway.siriusxm.com", loggerFactory, contentRootPath, username, password);
        sxmSessionService = new SxmSessionService(session, loggerFactory.CreateLogger<SxmSessionService>(), tokenSource, null);
        sxmSessionService.InitializeActivityTimer();

        // init services
        encryptionService = new HlsEncryptionService(session, logger);
        playlistService = new PlaylistService(loggerFactory.CreateLogger<PlaylistService>());
        metadataService = new MetadataService(
            loggerFactory.CreateLogger<MetadataService>(),
            session,
            sxmSessionService,
            playlistService,
            tokenSource.Token);
        metadataService.StartTimeoutHandler();

        // pass a provider for now-playing into IcecastStreamer
        icecastStreamer = new IcecastStreamer(logger, metadataService, this);

        // Configure Polly resilience pipeline for HTTP retries
        httpRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response => !response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Forbidden)
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .Handle<HttpRequestException>(),
                OnRetry = args =>
                {
                    logger.LogWarning($"HTTP request retry attempt {args.AttemptNumber} after {args.RetryDelay.TotalSeconds:F1}s delay. Outcome: {args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString()}");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public void Dispose()
    {
        StopProgressTimer();
        _session?.Dispose();
        tokenSource.Cancel();
        sxmSessionService.Dispose();
    }

    public async Task<List<ChannelItemData>> GetChannelsAsync()
    {
        if (_allChannels != null) return _allChannels;
        await sxmSessionService.LoginIfNecessary(nameof(GetChannelsAsync));
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
        await sxmSessionService.LoginIfNecessary(nameof(GetFavoritesAsync));
        var favoriteData = await session.apiClient.GetLibraryAsync(session.GetKey(), CancellationToken.None);
        var favorites = favoriteData.AllDataMap;
        return favorites?.Select(m => m.Key);
    }

    public virtual NowPlayingData? GetNowPlaying() => metadataService.GetNowPlaying();

    public const string CURRENT_ID = "current";

    private string currentChannelFile;

    private async Task<ChannelItemData?> GetCurrentChannel()
    {
        if (_currentChannel == null)
        {
            _currentChannel = await Tools.ReadJsonFile<ChannelItemData>(currentChannelFile, logger);
        }
        return _currentChannel;
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
        var nowPlaying = metadataService.GetNowPlaying();
        if (_currentChannel is null || nowPlaying is null || !HasActiveClient())
        {
            return false;
        }

        var isChannelExtra = _currentChannel.Entity.Type != "channel-linear";
        var type = isChannelExtra ? "channel-xtra" : "channel-linear";
        var itemType = isChannelExtra ? "xtra-channel-track" : "cut-linear";
        Position position;
        Position2 position2;
        Actions3 action;
        var hasStarted = false;
        if (isChannelExtra)
        {
            position = new Position() { ElapsedTimeMs = (startTs is null) ? 0 : (DateTimeOffset.Now - startTs.Value).TotalMilliseconds };
            position2 = new Position2() { ElapsedTimeMs = position.ElapsedTimeMs };
        }
        else
        {
            var audioTS = metadataService.AudioOriginalTimestamp;
            if (audioTS is null)
            {
                return false;
            }
            position = new Position() { AbsoluteTime = audioTS.Value.ToString("o") };
            position2 = new Position2() { AbsoluteTime = position.AbsoluteTime };
        }
        if (_currentChannel.Entity.Id is null || nowPlaying.id is null)
        {
            logger.LogWarning("Cannot send progress action - no current channel or no nowPlaying data");
            return false;
        }
        if (_channelHasChanged)
        {
            _channelHasChanged = false;
            startTs = DateTimeOffset.UtcNow;
            action = new()
            {
                Start = new()
                {
                    Source = new() { Type = type, Id = _currentChannel.Entity.Id },
                    Item = new() { Type = itemType, Id = nowPlaying.id },
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
                    Item = new() { Type = itemType, Id = nowPlaying.id },
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
        await sxmSessionService.LoginIfNecessary(nameof(GetStreamPlaylist));
        sxmSessionService.StartStatusChecks();
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

        try
        {
            var playlist = await playlistService.GetStreamPlaylistAsync(
                channelId,
                CURRENT_ID,
                listener,
                alias,
                useCache,
                currentChannel,
                listener?.IsPrimary ?? false,
                async selectedChannelId => await SetCurrentChannel(selectedChannelId),
                async selectedChannelId => await playlistService.GetProxyPlaylistUrlAsync(
                    selectedChannelId,
                    selected => TuneSource(selected),
                    async url => await GetHttpResponseMessage(url, getQueryParameters())),
                async url => await GetHttpResponseMessage(url, getQueryParameters()),
                async (selectedChannelId, ts) => await metadataService.GetNowPlaying(selectedChannelId, ts, GetChannelsAsync),
                metadataService.GetNowPlaying());

            avgSegmentDuration = playlistService.AverageSegmentDuration;
            return playlist;
        }
        catch (ApiException ex)
        {
            logger.LogWarning(ex, $"GetRealPlaylist data - Forbidden error - retrying - retries={retries}");
            await session.ReLogin();
            sxmSessionService.InitializeActivityTimer();
            return await GetStreamPlaylist(channelId, listener, alias, useCache: false, retries: retries + 1);
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
            // Cuts will refresh on next metadata request
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

        if (!playlistService.TryGetStream(channelId, out var stream) || stream is null)
        {
            // Try to populate from current tuning info
            try
            {
                _ = await playlistService.GetProxyPlaylistUrlAsync(
                    channelId,
                    selected => TuneSource(selected),
                    async url => await GetHttpResponseMessage(url, getQueryParameters()));
            }
            catch { }

            playlistService.TryGetStream(channelId, out stream);
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

        try
        {
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
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error fetching/decrypting segment {segmentId}");
            throw;
        }
    }

    public void RegisterNowPlayingListener(Action<NowPlayingData> listener)
    {
        metadataService.RegisterNowPlayingListener(listener);
    }

    private async Task SetNowPlayingFromSegment(SXMSegment segment, bool retry = true)
    {
        await metadataService.SetNowPlayingFromSegment(segment, GetChannelsAsync, mqttServer, retry);
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

    private static string[] SplitLines(string? res)
    {
        return res.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }




    private async Task<HttpResponseMessage?> GetHttpResponseMessage(string url, Dictionary<string, string> parameters)
    {
        var builder = new UriBuilder(url);
        builder.Query = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

        return await httpRetryPipeline.ExecuteAsync(async ct =>
        {
            var request = new HttpRequestMessage() { RequestUri = builder.Uri, Method = HttpMethod.Get };
            ConfigureRequest(request);

            var response = await session.GetHttpClient().SendAsync(request, ct);

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
        }, tokenSource.Token);
    }

    private PeriodicTimer? progressTimer;
    private Task? progressTimerTask;
    private SemaphoreSlim progressSemamphore = new SemaphoreSlim(1, 1);

    private void StopProgressTimer()
    {
        if (!progressSemamphore.Wait(TimeSpan.FromSeconds(2)))
        {
            logger.LogWarning("Could not acquire progress timer semaphore - skipping progress timer stop");
            return;
        }

        try
        {
            progressTimer?.Dispose();
            progressTimer = null;
        }
        finally
        {
            progressSemamphore.Release();
        }
    }

    private void StartProgressTimer(bool isChannelChanged)
    {
        if (!progressSemamphore.Wait(TimeSpan.FromSeconds(2)))
        {
            logger.LogWarning("Could not acquire progress timer semaphore - skipping progress timer start");
            return;
        }
        try
        {
            if (progressTimerTask is not null && !progressTimerTask.IsCompleted)
            {
                if (isChannelChanged && progressTimer is not null)
                {
                    _channelHasChanged = true;
                    progressTimer.Period = TimeSpan.FromSeconds(1);
                }
                return;
            }

            _channelHasChanged = isChannelChanged;

            progressTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            progressTimerTask = Task.Run(async () =>
            {
                try
                {
                    while (!tokenSource.IsCancellationRequested)
                    {
                        if (!HasActiveClient())
                        {
                            logger.LogDebug("Stopping progress timer - no active clients");
                            break;
                        }

                        var hasStarted = await SendProgressAction();
                        if (hasStarted && progressTimer is not null)
                        {
                            progressTimer.Period = TimeSpan.FromSeconds(60);
                        }

                        if (progressTimer is null)
                        {
                            break;
                        }

                        var hasNextTick = await progressTimer.WaitForNextTickAsync(tokenSource.Token);
                        if (!hasNextTick)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
                {
                }
                finally
                {
                    if (progressSemamphore.Wait(TimeSpan.FromSeconds(2)))
                    {
                        try
                        {
                            progressTimer?.Dispose();
                            progressTimer = null;
                            progressTimerTask = null;
                        }
                        finally
                        {
                            progressSemamphore.Release();
                        }
                    }
                    else
                    {
                        progressTimerTask = null;
                    }
                }
            });
        }
        finally
        {
            progressSemamphore.Release();
        }
    }

    private bool _channelHasChanged;
    private double? avgSegmentDuration;

    public int? ICYMetaInt { get; internal set; }
    public bool DisableICYMetadata { get; internal set; } = false;

    private async Task<Streams> TuneSource(string channelId, int retries = 0)
    {
        if (retries >= 5)
        {
            logger.LogError($"Too many retries");
            throw new InvalidOperationException("Too many retries");
        }
        await sxmSessionService.LoginIfNecessary(nameof(TuneSource));
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
                metadataService.UpdateCutsFromStream(channelId, stream1.Metadata?.Live?.Items!.ToList());
                return stream1;
            }
            else
            {
                //var metadata = stream1.Metadata!.
                return stream1;
            }
        }
        catch (HttpRequestException hex)
        {
            logger.LogWarning(hex, $"HTTP error during TuneSource - {hex.Message} - retrying - retries={retries}");
            await Task.Delay(TimeSpan.FromSeconds(5 * (retries + 1)), tokenSource.Token);
            await session.ReLogin();
            sxmSessionService.InitializeActivityTimer();
            return await TuneSource(channelId, retries + 1);
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
                sxmSessionService.InitializeActivityTimer();
                return await TuneSource(channelId, retries + 1);
            }
        }
    }

    private Dictionary<string, string> getQueryParameters() => new Dictionary<string, string> { };

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

        if (!HasActiveClient())
        {
            StopProgressTimer();
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

        }
        var np = GetNowPlaying();
        if (np is not null)
        {
            ctx.Response.Headers["icy-name"] = $"Sirius XM - {current.Entity.ChannelName}";
        }        // Conservative headers for legacy clients
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
            var segmentQueue = System.Threading.Channels.Channel.CreateUnbounded<global::SXMPlayer.SegmentWorkItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            icecastStreamer.StartHLSReader(segmentQueue.Writer, GetCurrentChannel, listener, playlistRefreshSource.Token, ct);

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
            if (playlistRefreshSource.IsCancellationRequested)
            {
                logger.LogInformation("Playlist refresh requested, restarting producer.");
                continue;
            }
        }
    }

}
