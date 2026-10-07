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
using System.Diagnostics;

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
    // Metadata fields removed - now in MetadataService
    private HttpClientHandler? _session;
    // Metadata fields removed - now in MetadataService
    private ILogger<SiriusXMPlayer> logger;
    private List<SXMListener> clients = new List<SXMListener>();
    // Metadata fields removed - now in MetadataService

    private CancellationTokenSource tokenSource = new CancellationTokenSource();
    private CancellationTokenSource channelChangedSource = new CancellationTokenSource();
    private readonly object playlistRefreshLock = new();
    private DateTimeOffset lastPlaylistRefresh = DateTimeOffset.MinValue;
    private static readonly TimeSpan PlaylistRefreshDebounce = TimeSpan.FromSeconds(5);
    // Process-wide escalation state (consecutive stale-refresh counter and the
    // secondary-URL fallback flag) lives in the DI-singleton PlayerState so the
    // fetch and refresh paths share one thread-safe owner.
    private readonly PlayerState playerState;
    private CacheManager cacheManager = null!;


    // New services
    private readonly HlsEncryptionService encryptionService;
    private readonly PlaylistService playlistService;
    private readonly IcecastStreamer icecastStreamer;
    private readonly IStreamHttpClient streamHttpClient;
    private readonly MetadataService metadataService;
    private readonly ProgressTimerManager progressTimerManager;

    //status - every 50 seconds
    //https://api.edge-gateway.siriusxm.com/playback/stream-enforcement/v1/status

    // Test-only seam: lets subclasses in the test project stand in for a real player
    // (overriding virtual members such as GetSegment / RequestPlaylistRefresh) without
    // running the full dependency-wiring constructor.
    protected SiriusXMPlayer(ILogger<SiriusXMPlayer> logger, PlaylistService playlistService, PlayerState? playerState = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        this.playerState = playerState ?? new PlayerState();
        username = null!;
        password = null!;
        session = null!;
        sxmSessionService = null!;
        encryptionService = null!;
        icecastStreamer = null!;
        streamHttpClient = null!;
        metadataService = null!;
        progressTimerManager = null!;
    }

    public SiriusXMPlayer(IConfiguration configuration,
                    ILogger<SiriusXMPlayer> logger,
                    ILoggerFactory loggerFactory,
                    IWebHostEnvironment hostingEnvironment,
                    PlayerState playerState)
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

        this.playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));

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
        var currentChannelFile = Path.Combine(contentRootPath, "currentChannel.json");
        session = new APISession("https://api.edge-gateway.siriusxm.com", loggerFactory, contentRootPath, username, password);
        sxmSessionService = new SxmSessionService(session, loggerFactory.CreateLogger<SxmSessionService>(), tokenSource, null);
        sxmSessionService.InitializeActivityTimer();

        // init services
        encryptionService = new HlsEncryptionService(session, logger);
        var streamHttpClient = new StreamHttpClient(
            session,
            tokenSource,
            loggerFactory.CreateLogger<StreamHttpClient>());
        this.streamHttpClient = streamHttpClient;
        metadataService = new MetadataService(
            loggerFactory.CreateLogger<MetadataService>(),
            session,
            sxmSessionService,
            currentChannelFile,
            tokenSource.Token);
        metadataService.StartTimeoutHandler();

        var streamTuner = new StreamTuner(
            sxmSessionService,
            session,
            metadataService,
            loggerFactory.CreateLogger<StreamTuner>(),
            tokenSource);
        playlistService = new PlaylistService(
            loggerFactory.CreateLogger<PlaylistService>(),
            metadataService,     // ICurrentChannelService
            streamTuner,         // IStreamTuner
            streamHttpClient,    // IStreamHttpClient
            metadataService,     // INowPlayingProvider
            playerState);        // PlayerState

        progressTimerManager = new ProgressTimerManager(
            loggerFactory.CreateLogger<ProgressTimerManager>(),
            HasActiveClient,
            metadataService,
            session,
            tokenSource.Token);

        // pass a provider for now-playing into IcecastStreamer
        icecastStreamer = new IcecastStreamer(logger, metadataService, this);

        // Final wiring step: wire the reverse read-only edge (breaks the construction cycle),
        // then subscribe exactly once to the channel-change signal.
        metadataService.SetStreamTimeMap(playlistService);
        metadataService.ChannelChanged += OnChannelChanged;
    }

    public void Dispose()
    {
        progressTimerManager.Dispose();
        _session?.Dispose();
        tokenSource.Cancel();
        sxmSessionService.Dispose();
    }

    public async Task<List<ChannelItemData>> GetChannelsAsync()
    {
        return await metadataService.GetChannelsAsync();
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

    public virtual async Task<string?> GetStreamPlaylist(string channelId, SXMListener? listener, string? alias = null, bool useCache = true, int retries = 0)
    {
        var start = Stopwatch.GetTimestamp();
        logger.LogDebug("GetStreamPlaylist start - channelId={ChannelId} alias={Alias} useCache={UseCache} listenerIp={ListenerIp} retries={Retries}",
            channelId,
            alias,
            useCache,
            listener?.IPAddress,
            retries);

        await sxmSessionService.LoginIfNecessary(nameof(GetStreamPlaylist));
        sxmSessionService.StartStatusChecks();
        var currentChannel = await metadataService.GetCurrentChannelAsync();
        logger.LogDebug("Current channel before playlist check - requested={RequestedChannelId} current={CurrentChannelId}", channelId, currentChannel?.Entity.Id);

        var isChannelChange = channelId != CURRENT_ID && channelId != currentChannel?.Entity.Id;
        if (isChannelChange)
        {
            logger.LogInformation($"Changing channel to {channelId}");
        }

        if (listener is not null)
        {
            listener.LastPlaylistRequest = DateTimeOffset.Now;
        }

        StartProgressTimer(isChannelChange);

        try
        {
            var playlist = await playlistService.GetStreamPlaylistAsync(channelId, CURRENT_ID, alias, useCache);

            avgSegmentDuration = playlistService.AverageSegmentDuration;
            logger.LogDebug("GetStreamPlaylist completed - channelId={ChannelId} playlistLength={PlaylistLength} avgSegmentDuration={AvgSegmentDuration} elapsedMs={ElapsedMs:F2}",
                channelId,
                playlist?.Length,
                avgSegmentDuration,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);
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

    // Single subscriber to MetadataService.ChannelChanged (Func<string, Task> shape; NOT async
    // void / EventHandler). Reproduces the former SetCurrentChannel hasChanged side effects in
    // the same order: the event only fires on a genuine change, so the hasChanged guard is implicit.
    private Task OnChannelChanged(string channelId)
    {
        logger.LogInformation($"Setting current channel to {channelId}");
        progressTimerManager.MarkChannelChanged();
        ResetStreamUrlFallback();
        channelChangedSource.Cancel();
        channelChangedSource = new CancellationTokenSource();
        // Cuts will refresh on next metadata request
        return Task.CompletedTask;
    }

    /// <summary>
    /// Treats a stale playlist (e.g. a segment returned 404) as a signal to re-fetch the
    /// current channel's stream playlist and restart the producer against fresh data.
    /// Reuses the existing <see cref="channelChangedSource"/> restart path. Debounced so a
    /// burst of 404s triggers at most one refresh within <see cref="PlaylistRefreshDebounce"/>.
    /// </summary>
    // Test-only accessor for the current channel-changed token, used to observe that a
    // playlist refresh cancelled-and-replaced the restart signal.
    internal CancellationToken ChannelChangedTokenForTest => channelChangedSource.Token;

    public virtual void RequestPlaylistRefresh(string reason)
    {
        lock (playlistRefreshLock)
        {
            var now = DateTimeOffset.Now;
            if (now - lastPlaylistRefresh < PlaylistRefreshDebounce)
            {
                logger.LogDebug("Skipping playlist refresh (debounced) - reason={Reason}", reason);
                return;
            }

            lastPlaylistRefresh = now;

            // After repeated stale refreshes the primary stream URL isn't recovering;
            // fall back to the secondary URL for the next fetch. PlayerState increments
            // the counter and, on escalation, flips the flag and resets the counter so
            // the secondary gets a clean run before considering further escalation.
            // Capture the count the registration observed (pre-reset) so the log mirrors
            // the previous behavior, where the incremented value was logged before any reset.
            var (escalated, observedCount) = playerState.RegisterStaleRefresh();
            logger.LogInformation("Playlist refresh requested - reason={Reason} consecutiveStaleRefreshes={Count}",
                reason,
                observedCount);

            if (escalated)
            {
                logger.LogWarning(
                    "Repeated stale playlist refreshes ({Threshold}); falling back to secondary stream URL - reason={Reason}",
                    PlayerState.SecondaryFallbackRefreshThreshold,
                    reason);
            }

            playlistService.InvalidatePlaylistCache();
            channelChangedSource.Cancel();
            channelChangedSource = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Resets the stale-refresh escalation state (consecutive-refresh counter and the
    /// secondary-URL fallback flag) back to the primary stream URL. Called when a genuine
    /// channel change gives us a fresh, known-good starting point.
    /// </summary>
    private void ResetStreamUrlFallback()
    {
        lock (playlistRefreshLock)
        {
            if (playerState.ResetStreamUrlFallback())
            {
                logger.LogInformation("Resetting stream URL selection back to primary after channel change.");
            }
        }
    }

    public virtual async Task<Stream> GetSegment(string channelId, string version, string segmentId, SXMListener? client)
    {
        UpdateClientActivity();
        var currentChannel = await metadataService.GetCurrentChannelAsync();
        if (channelId == CURRENT_ID && currentChannel != null)
        {
            //set the real channel
            channelId = currentChannel.Entity.Id!;
        }
        // Channel mismatch check - don't allow segment requests to change channels
        // Only GetStreamPlaylist with explicit channel IDs should trigger SetCurrentChannel
        if (channelId != currentChannel?.Entity.Id)
        {
            throw new InvalidOperationException($"Channel mismatch: requested {channelId} but current is {currentChannel?.Entity.Id}. Channel must be set via playlist request first.");
        }

        if (!playlistService.TryGetStream(channelId, out var stream) || stream is null)
        {
            // Try to populate from current tuning info
            try
            {
                _ = await playlistService.GetProxyPlaylistUrlAsync(
                    channelId,
                    useSecondary: playerState.UseSecondaryStreamUrl);
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
        var url = $"{stream.path}{version}/{segmentId}?CMDC=";

        try
        {
            var output = await streamHttpClient.GetAsync(url);
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
        catch (SegmentNotFoundException)
        {
            // 404 means the playlist is stale; the producer logs this once and refreshes.
            // Do not emit a per-attempt error stack trace here.
            throw;
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
        await metadataService.SetNowPlayingFromSegment(segment, retry);
    }

    public virtual async Task<byte[]> GetDecryptionKey(string guid)
    {
        return await encryptionService.GetDecryptionKey(guid);
    }

    private void StopProgressTimer()
    {
        progressTimerManager.Stop();
    }

    private void StartProgressTimer(bool isChannelChanged)
    {
        progressTimerManager.Start(isChannelChanged);
    }
    private double? avgSegmentDuration;

    public int? ICYMetaInt { get; internal set; }
    public bool DisableICYMetadata { get; internal set; } = false;

    public async Task<EntityData?> GetChannelFromFilename(string fileName)
    {
        var channels = await metadataService.GetChannelsAsync();
        return channels.First(c => c.Entity.Filename == fileName).Entity;
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
        var current = await metadataService.GetCurrentChannelAsync();
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
        var current = await metadataService.GetCurrentChannelAsync();
        if (current == null)
        {
            throw new InvalidOperationException("No channel data available");
        }
        string realChannelId = channelId == CURRENT_ID ? current?.Entity.Id ?? throw new InvalidOperationException("No channel selected") : channelId;
        if (channelId != CURRENT_ID && current?.Entity.Id != channelId)
        {
            await metadataService.SetCurrentChannelAsync(channelId); // raises ChannelChanged -> OnChannelChanged
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
        int? metaInt = injectMeta ? (ICYMetaInt ?? ICY_META_BLOCK) : null;

        if (injectMeta && metaInt.HasValue)
        {
            logger.LogInformation($"Injecting ICY metadata every {metaInt.Value} bytes for client {ctx.Connection.RemoteIpAddress} (UA: {userAgent})");
            ctx.Response.Headers["icy-metaint"] = metaInt.Value.ToString();
        }
        if (current is not null)
        {
            var sanitizedChannelName = IcyHeaderSanitizer.SanitizeHeaderValue(current.Entity.ChannelName);
            ctx.Response.Headers["icy-name"] = $"Sirius XM - {sanitizedChannelName}";
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

        int bytesUntilMeta = metaInt ?? int.MaxValue;

        while (!ct.IsCancellationRequested)
        {
            // Force to resend metadata on next segment
            icecastStreamer.ClearMetadataState();

            var segmentQueue = System.Threading.Channels.Channel.CreateUnbounded<global::SXMPlayer.SegmentWorkItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // Snapshot the current channel-changed source for this iteration. RequestPlaylistRefresh
            // cancels this source AND replaces the field with a fresh (uncancelled) CTS, so after a
            // refresh the live field no longer reports cancellation. We must observe cancellation on
            // the SAME source we handed to the producer, otherwise we miss the restart signal and the
            // producer stays stopped (e.g. the secondary-URL fallback appeared to require a player
            // restart to take effect).
            var iterationChannelChanged = channelChangedSource;

            await icecastStreamer.StartHLSReader(segmentQueue.Writer, listener, iterationChannelChanged.Token, ct);

            var receivedAnyData = false;

            try
            {
                while (await segmentQueue.Reader.WaitToReadAsync(ct))
                {
                    while (segmentQueue.Reader.TryRead(out var item))
                    {
                        receivedAnyData = true;

                        // Data is already decrypted by the producer
                        if (item.AudioData is not null)
                        {
                            bytesUntilMeta = await icecastStreamer.WriteWithIcyAsync(item.AudioData.Value, ctx, injectMeta, metaInt ?? int.MaxValue, bytesUntilMeta, ct);
                            listener.LastActivity = DateTimeOffset.Now;
                        }
                        else
                        {
                            logger.LogWarning($"Received segment {item.SegmentName} with no data");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Icecast stream ended for client {ClientIp}", ctx.Connection.RemoteIpAddress);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in Icecast streaming consumer.");
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Check the snapshot (not the live field): a refresh cancels this exact source even
            // though the field has already been swapped to a fresh one. Looping restarts the
            // producer, whose next GetStreamPlaylist re-runs GetProxyPlaylistUrlAsync and picks up
            // any secondary-URL fallback.
            if (iterationChannelChanged.IsCancellationRequested)
            {
                logger.LogInformation("Playlist refresh requested, restarting producer.");
                continue;
            }

            if (!receivedAnyData)
            {
                logger.LogDebug("No HLS segments were produced for this iteration; waiting for producer activity before restart.");
                using var waitForDataCts = CancellationTokenSource.CreateLinkedTokenSource(ct, iterationChannelChanged.Token);
                try
                {
                    await icecastStreamer.WaitForProducerActivityAsync(waitForDataCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !iterationChannelChanged.IsCancellationRequested)
                {
                    // Ignore transient wait cancellations and continue the loop.
                }
            }
        }
    }

}
