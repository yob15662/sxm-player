using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;

namespace SXMPlayer;

/// <summary>
/// Service responsible for managing now playing information and metadata from SiriusXM streams.
/// </summary>
public class MetadataService : IDisposable
{
    public const string MQTT_TOPIC = "SiriusXM/NowPlaying";
    public const string MQTT_CHANNEL = $"{MQTT_TOPIC}/Channel";
    public const string MQTT_ARTIST = $"{MQTT_TOPIC}/Artist";
    public const string MQTT_TRACK = $"{MQTT_TOPIC}/Track";

    private readonly ILogger<MetadataService> logger;
    private readonly APISession session;
    private readonly SxmSessionService sxmSessionService;
    private readonly PlaylistService playlistService;
    private readonly CancellationToken cancellationToken;

    // Now playing state
    private NowPlayingData? _nowPlaying;
    private Action<NowPlayingData>? _nowPlayingListener;
    private DateTimeOffset? _audioOriginalTS;
    private DateTimeOffset? _currentSelectionTS;
    private DateTimeOffset? _lastNowPlayingListenersUpdate;

    // Cuts management
    private List<MetadataItem>? allCutsCurrentChannel;
    private string? allCutsChannelInfo;
    private readonly object _allCutsLock = new object();
    private int cutsRefreshed;

    // Live now playing tracking
    private DateTimeOffset? liveNowPlayingExpiry;

    // Timeout monitoring
    private Task? timeoutMonitorTask;

    public MetadataService(
        ILogger<MetadataService> logger,
        APISession session,
        SxmSessionService sxmSessionService,
        PlaylistService playlistService,
        CancellationToken cancellationToken)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.sxmSessionService = sxmSessionService ?? throw new ArgumentNullException(nameof(sxmSessionService));
        this.playlistService = playlistService ?? throw new ArgumentNullException(nameof(playlistService));
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the current now playing information.
    /// </summary>
    public virtual NowPlayingData? GetNowPlaying() => _nowPlaying;

    /// <summary>
    /// Gets the audio timestamp from the current segment.
    /// </summary>
    public DateTimeOffset? AudioOriginalTimestamp => _audioOriginalTS;

    /// <summary>
    /// Registers a listener to be notified when now playing information changes.
    /// </summary>
    public void RegisterNowPlayingListener(Action<NowPlayingData> listener)
    {
        _nowPlayingListener = listener;
    }

    /// <summary>
    /// Starts the timeout handler that monitors for inactive segments.
    /// </summary>
    public void StartTimeoutHandler()
    {
        if (timeoutMonitorTask is not null)
        {
            return;
        }

        timeoutMonitorTask = Task.Factory.StartNew(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    if (_currentSelectionTS != null && DateTimeOffset.Now - _currentSelectionTS > TimeSpan.FromSeconds(120))
                    {
                        sxmSessionService.MarkInactivityStart(DateTimeOffset.Now);
                        _nowPlaying = new NowPlayingData("-", "-", "-", null);
                        _nowPlayingListener?.Invoke(_nowPlaying);
                        logger.LogInformation($"Timeout detected for segments");
                        _currentSelectionTS = null;
                    }
                }
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Refreshes the metadata cuts for a specific channel.
    /// </summary>
    private async Task RefreshAllCuts(string channel)
    {
        var ts = DateTimeOffset.Now;
        var start = ts.AddMinutes(-10).AddHours(-3);
        var end = ts.AddMinutes(1);
        
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

    private string FormatToISO8601(DateTimeOffset dateTime)
    {
        return dateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    /// <summary>
    /// Gets now playing information for a specific channel and timestamp.
    /// </summary>
    public async Task<(string artist, string title, string? id)?> GetNowPlaying(
        string channelId, 
        DateTimeOffset? ts, 
        Func<Task<List<ChannelItemData>>> getChannelsAsync,
        bool tryRefresh = true)
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
                return await GetNowPlaying(channelId, ts, getChannelsAsync, false);
            }
            var channelInfo = (await getChannelsAsync()).First(c => c.Entity.Id == channelId);
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
                var channelInfo = (await getChannelsAsync()).First(c => c.Entity.Id == channelId);
                return (channelInfo.Entity.ChannelName, channelInfo.Entity.Texts?.Description?.Default ?? "-", null);
            }
            await RefreshAllCuts(channelId);
            cutsRefreshed++;
            return await GetNowPlaying(channelId, ts, getChannelsAsync, false);
        }
        
        cutsRefreshed = 0;
        
        if (current is null && utcTime <= liveNowPlayingExpiry?.ToUniversalTime())
        {
            current = currentCuts.FirstOrDefault(ct => ct.StartTime == ct.EndTime);
        }

        if (current is null)
        {
            current = currentCuts.FirstOrDefault(ct => ct.StartTime == ct.EndTime);
            if (current is not null && ts is not null)
            {
                liveNowPlayingExpiry = current?.EndTime.AddSeconds((ts.Value - current.EndTime).TotalSeconds + 30);
            }
        }
        else
        {
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
            var channelInfo = (await getChannelsAsync()).First(c => c.Entity.Id == channelId);
            logger.LogWarning($"Invalid now playing data - channel={channelId} ts={ts?.ToLocalTime()} - first={firstCut?.StartTime.ToLocalTime()} - last={lastCut?.StartTime.ToLocalTime()}");
            return (channelInfo.Entity.ChannelName, channelInfo.Entity.Texts?.Description?.Default ?? "-", null);
        }
    }

    /// <summary>
    /// Sets now playing information from an HLS segment.
    /// </summary>
    public async Task SetNowPlayingFromSegment(
        SXMSegment segment, 
        Func<Task<List<ChannelItemData>>> getChannelsAsync,
        string? mqttServer,
        bool retry = true)
    {
        _audioOriginalTS = null;
        var channels = await getChannelsAsync();
        var prevNowPlaying = _nowPlaying;
        _nowPlaying = null;
        
        if (playlistService.StreamTimeMap.TryGetValue(segment.segment, out var audioTS))
        {
            _audioOriginalTS = audioTS;
            var trackInfo = await GetNowPlaying(segment.stream.channel, _audioOriginalTS, getChannelsAsync);
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
            {
                await RefreshAllCuts(segment.stream.channel);
                await SetNowPlayingFromSegment(segment, getChannelsAsync, mqttServer, false);
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

    /// <summary>
    /// Updates cuts data from stream metadata (for linear channels).
    /// </summary>
    public void UpdateCutsFromStream(string channelId, List<MetadataItem>? items)
    {
        if (items is null)
        {
            return;
        }

        lock (_allCutsLock)
        {
            allCutsChannelInfo = channelId;
            allCutsCurrentChannel = items;
        }
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}
