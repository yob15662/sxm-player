using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Threading;

namespace SXMPlayer;

public sealed class ProgressTimerManager : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<ProgressTimerManager> _logger;
    private readonly Func<bool> _hasActiveListeners;
    private readonly MetadataService _metadataService;
    private readonly APISession _session;
    private readonly Func<Actions3, CancellationToken, Task<Response15>>? _sendActionOverride;
    private readonly CancellationToken _shutdownToken;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    private int _logicalClockCounter;
    private DateTimeOffset? _startTs;
    private bool _channelHasChanged;

    public ProgressTimerManager(
        ILogger<ProgressTimerManager> logger,
        Func<bool> hasActiveListeners,
        MetadataService metadataService,
        APISession session,
        CancellationToken shutdownToken,
        TimeSpan? interval = null,
        Func<Actions3, CancellationToken, Task<Response15>>? sendActionOverride = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hasActiveListeners = hasActiveListeners ?? throw new ArgumentNullException(nameof(hasActiveListeners));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _shutdownToken = shutdownToken;
        _interval = interval ?? DefaultInterval;
        _sendActionOverride = sendActionOverride;

        if (_interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Progress interval must be greater than zero.");
        }
    }

    public void MarkChannelChanged()
    {
        ThrowIfDisposed();
        _channelHasChanged = true;
    }

    public void Start(bool isChannelChanged)
    {
        ThrowIfDisposed();

        if (isChannelChanged)
        {
            _channelHasChanged = true;
        }

        if (!_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            _logger.LogWarning("Could not acquire progress timer lock - skipping start");
            return;
        }

        try
        {
            var isRunning = _loopTask is not null && !_loopTask.IsCompleted;
            if (!isRunning)
            {
                _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
                _wakeSignal.Release();
                return;
            }

            if (isChannelChanged)
            {
                _wakeSignal.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        if (!_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            _logger.LogWarning("Could not acquire progress timer lock - skipping stop");
            return;
        }

        try
        {
            _loopCts?.Cancel();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_wakeSignal.CurrentCount > 0)
                {
                    await _wakeSignal.WaitAsync(cancellationToken);
                }

                if (!await TrySendProgressAsync(cancellationToken))
                {
                    break;
                }

                var delayTask = Task.Delay(_interval, cancellationToken);
                var wakeTask = _wakeSignal.WaitAsync(cancellationToken);
                await Task.WhenAny(delayTask, wakeTask);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (_gate.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    _loopCts?.Dispose();
                    _loopCts = null;
                    _loopTask = null;
                }
                finally
                {
                    _gate.Release();
                }
            }
            else
            {
                _loopTask = null;
            }
        }
    }

    private async Task<bool> TrySendProgressAsync(CancellationToken cancellationToken)
    {
        if (!_hasActiveListeners())
        {
            _logger.LogDebug("Stopping progress timer - no active listeners");
            return false;
        }

        try
        {
            await SendProgressAction(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (IsTransientSendFailure(ex))
        {
            _logger.LogInformation(ex, "Transient error sending progress action; will retry on next interval");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending progress action");
            return true;
        }
    }

    private static bool IsTransientSendFailure(Exception ex)
    {
        if (ex is TaskCanceledException)
        {
            return true;
        }

        if (ex is HttpRequestException)
        {
            return true;
        }

        return ex.InnerException is HttpRequestException;
    }

    private async Task SendProgressAction(CancellationToken cancellationToken)
    {
        var currentChannel = _metadataService.GetCurrentChannel();
        var nowPlaying = _metadataService.GetNowPlaying();
        if (currentChannel is null || nowPlaying is null || !_hasActiveListeners())
        {
            return;
        }

        var isChannelExtra = currentChannel.Entity.Type != "channel-linear";
        var type = isChannelExtra ? "channel-xtra" : "channel-linear";
        var itemType = isChannelExtra ? "xtra-channel-track" : "cut-linear";

        Position position;
        Position2 position2;

        if (isChannelExtra)
        {
            if (_startTs is null)
            {
                _startTs = DateTimeOffset.UtcNow;
            }

            position = new Position { ElapsedTimeMs = (DateTimeOffset.UtcNow - _startTs.Value).TotalMilliseconds };
            position2 = new Position2 { ElapsedTimeMs = position.ElapsedTimeMs };
        }
        else
        {
            var audioTS = _metadataService.GetAudioOriginalTimestamp();
            if (audioTS is null)
            {
                return;
            }

            position = new Position { AbsoluteTime = audioTS.Value.ToString("o") };
            position2 = new Position2 { AbsoluteTime = position.AbsoluteTime };
        }

        if (currentChannel.Entity.Id is null || nowPlaying.id is null)
        {
            _logger.LogWarning("Cannot send progress action - no current channel or no nowPlaying data");
            return;
        }

        Actions3 action;
        if (_channelHasChanged)
        {
            _channelHasChanged = false;
            _startTs = DateTimeOffset.UtcNow;
            action = new Actions3
            {
                Start = new()
                {
                    Source = new() { Type = type, Id = currentChannel.Entity.Id },
                    Item = new() { Type = itemType, Id = nowPlaying.id },
                    Position = position2,
                    SourceTimestamp = DateTimeOffset.UtcNow.ToString("o"),
                    LogicalClock = new() { Counter = _logicalClockCounter, Epoch = 0 }
                }
            };
        }
        else
        {
            action = new Actions3
            {
                Progress = new()
                {
                    Source = new() { Type = type, Id = currentChannel.Entity.Id },
                    Item = new() { Type = itemType, Id = nowPlaying.id },
                    Position = position,
                    SourceTimestamp = DateTimeOffset.UtcNow.ToString("o"),
                    LogicalClock = new() { Counter = _logicalClockCounter, Epoch = 0 }
                }
            };
        }

        _logicalClockCounter++;
        _logger.LogDebug("Sending progress action - {Action}", action);

        var response = _sendActionOverride is not null
            ? await _sendActionOverride(action, cancellationToken)
            : await _session.apiClient.ActionsAsync(new() { Actions = [action] }, cancellationToken);

        var failedIds = (response.FailedActions ?? Enumerable.Empty<object>())
            .Select(o => o?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (failedIds.Count > 0)
        {
            _logger.LogWarning("Failed to send progress action - {FailedIds} failed", string.Join(", ", failedIds));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        if (_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            try
            {
                _loopCts?.Dispose();
                _loopCts = null;
                _loopTask = null;
            }
            finally
            {
                _gate.Release();
            }
        }

        _gate.Dispose();
        _wakeSignal.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProgressTimerManager));
        }
    }
}
