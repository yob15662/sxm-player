using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SXMPlayer;

/// <summary>
/// Produces decrypted HLS segments from playlists for streaming.
/// Fetches playlists, parses encryption keys, retrieves and decrypts segments,
/// then queues them for consumption.
/// </summary>
public class HlsSegmentProducer
{
    private readonly SiriusXMPlayer _player;
    private readonly ILogger _logger;
    private readonly object _producerLock = new();
    private readonly SegmentFanoutHub _fanout;
    private CancellationTokenSource? _producerStopCts;
    private Task? _producerTask;
    // The combined cancellation token the current producer task is running on (links the
    // caller-supplied channel-changed token with _producerStopCts). Liveness MUST be judged
    // against this token: a playlist refresh cancels the channel-changed source that is linked
    // into it, so this token reports cancellation the instant a refresh fires — before the
    // producer's finally has nulled _producerTask. Checking _producerStopCts or a freshly
    // supplied channelChangedCt instead would miss that signal and misclassify a dying producer
    // as still active.
    private CancellationToken _producerCombinedCt;
    private TaskCompletionSource<bool> _activitySignal = CreateActivitySignal();

    private static TaskCompletionSource<bool> CreateActivitySignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalActivity()
    {
        var current = Interlocked.Exchange(ref _activitySignal, CreateActivitySignal());
        current.TrySetResult(true);
    }

    public Task WaitForActivityAsync(CancellationToken cancellationToken)
    {
        var waitTask = Volatile.Read(ref _activitySignal).Task;
        return waitTask.WaitAsync(cancellationToken);
    }

    public HlsSegmentProducer(SiriusXMPlayer player, ILogger logger)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fanout = new SegmentFanoutHub(StopProducerIfIdle);
    }

    /// <summary>
    /// Registers a client writer with the shared HLS producer.
    /// Starts the shared producer when it is not already active.
    /// </summary>
    /// <remarks>
    /// A producer that has been cancelled (channel change or playlist refresh) but whose
    /// teardown has not yet completed is treated as <em>stopping</em>, not active. In that case
    /// this method awaits the old producer's completion before registering the new writer, so
    /// the old task's <c>_fanout.CompleteAll(...)</c> cannot complete the just-registered writer
    /// and leave the stream without a live producer. This is what lets a playlist refresh (e.g.
    /// the secondary-URL fallback) reliably restart production instead of requiring a full
    /// player restart.
    /// </remarks>
    /// <returns>True when the shared producer was already active (a new client was attached).</returns>
    public async Task<bool> StartProducer(
        ChannelWriter<SegmentWorkItem> writer,
        Func<Task<ChannelItemData?>> channelProvider,
        SXMListener listener,
        CancellationToken channelChangedCt,
        CancellationToken clientDisconnectToken)
    {
        Task? stoppingTask = null;

        lock (_producerLock)
        {
            if (IsProducerLive())
            {
                // Genuine attach-to-active-producer case.
                _fanout.Register(listener, writer, clientDisconnectToken);
                return true;
            }

            // Producer is absent, completed, or stopping (its combined token is cancelled but its
            // finally may not have run yet). If a task is still running, capture it so we can await
            // its teardown (its finally runs _fanout.CompleteAll and nulls _producerTask) BEFORE
            // registering the new writer — otherwise that teardown would complete our new writer.
            if (_producerTask is { IsCompleted: false })
            {
                stoppingTask = _producerTask;
            }
        }

        if (stoppingTask is not null)
        {
            // Awaited outside the lock so the old producer's finally (which also takes
            // _producerLock) can run to completion without deadlocking.
            try
            {
                await stoppingTask.ConfigureAwait(false);
            }
            catch
            {
                // The old producer's failure is already logged by RunProducerAsync; we only need
                // it to have finished its teardown before we start fresh.
            }
        }

        lock (_producerLock)
        {
            // Re-check: while awaiting the old teardown another caller may have already started a
            // fresh, live producer. If so, just attach to it instead of starting a second one.
            if (IsProducerLive())
            {
                _fanout.Register(listener, writer, clientDisconnectToken);
                return true;
            }

            _logger.LogInformation("Starting HLS segment producer.");
            _fanout.Register(listener, writer, clientDisconnectToken);
            _producerStopCts?.Dispose();
            _producerStopCts = new CancellationTokenSource();
            var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(channelChangedCt, _producerStopCts.Token);
            _producerCombinedCt = combinedCts.Token;
            _producerTask = RunProducerAsync(channelProvider, combinedCts);
            return false;
        }
    }

    /// <summary>
    /// A producer is live only when its task is running AND the combined token it runs on has not
    /// been cancelled. The combined token is linked to the caller's channel-changed source, so a
    /// playlist refresh (which cancels that source) makes this false immediately — the correct
    /// signal that the producer is stopping, even before its finally nulls the fields.
    /// Caller must hold <see cref="_producerLock"/>.
    /// </summary>
    private bool IsProducerLive()
        => _producerTask is { IsCompleted: false } && !_producerCombinedCt.IsCancellationRequested;

    /// <summary>
    /// Cancels producers for inactive clients.
    /// </summary>
    public void CancelProducersForInactiveClients(IEnumerable<SXMListener> inactiveClients)
    {
        foreach (var client in inactiveClients)
        {
            _logger.LogInformation($"Removing inactive client {client.IPAddress} from HLS producer fanout.");
            _fanout.Unregister(client);
        }
    }

    private async Task RunProducerAsync(
        Func<Task<ChannelItemData?>> channelProvider,
        CancellationTokenSource combinedCts)
    {
        var combinedCt = combinedCts.Token;
        long lastMediaSequence = -1;
        var processedSegments = new HashSet<string>();
        const int maxProcessedSegments = 50;
        string? lastChannelId = null;
        bool useCache = true;
        Exception? completionError = null;

        try
        {
            while (!combinedCt.IsCancellationRequested)
            {
                try
                {
                    string channelId = (await channelProvider() ?? throw new InvalidOperationException("No current channel")).Entity!.Id!;

                    if (lastChannelId != channelId)
                    {
                        if (lastChannelId is not null)
                        {
                            _logger.LogInformation($"Segment producer switching from channel '{lastChannelId}' to '{channelId}'.");
                        }
                        lastChannelId = channelId;
                        lastMediaSequence = -1;
                        processedSegments.Clear();
                    }

                    var playlist = await _player.GetStreamPlaylist(channelId, null, alias: channelId, useCache: useCache);
                    useCache = false;
                    if (string.IsNullOrEmpty(playlist))
                    {
                        await Task.Delay(500, combinedCt);
                        continue;
                    }

                    var lines = playlist.Split('\n');
                    byte[]? currentKey = null;
                    byte[]? currentIV = null;
                    long currentMediaSequence = -1;
                    double targetDuration = 2.0;

                    foreach (var line in lines)
                    {
                        var l = line.Trim();
                        if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(l["#EXT-X-MEDIA-SEQUENCE:".Length..], out currentMediaSequence);
                        }
                        else if (l.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                        {
                            double.TryParse(l["#EXT-X-TARGETDURATION:".Length..], out targetDuration);
                        }
                    }

                    if (lastMediaSequence == -1 && currentMediaSequence > 0)
                    {
                        lastMediaSequence = currentMediaSequence + lines.Count(s => s.Trim().EndsWith(".aac")) - 2;
                    }

                    long segmentSequence = currentMediaSequence;
                    var segmentsSent = 0;

                    foreach (var line in lines)
                    {
                        var l = line.Trim();
                        if (l.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                        {
                            (currentKey, currentIV) = await ParseEncryptionKeyAsync(l);
                        }
                        else if (l.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
                        {
                            if (segmentSequence > lastMediaSequence)
                            {
                                var segmentName = l.Split('/').Last();
                                if (processedSegments.Add(segmentName))
                                {
                                    var parts = l.Split('/');
                                    var version = parts[^2];

                                    byte[]? audioData = await FetchAndDecryptSegment(
                                        channelId, channelProvider, version, segmentName,
                                        currentKey, currentIV, segmentSequence, combinedCt);

                                    if (audioData is not null)
                                    {
                                        var item = new SegmentWorkItem(segmentName, version, segmentSequence, new Memory<byte>(audioData));
                                        await _fanout.BroadcastAsync(item, combinedCt);
                                        segmentsSent++;
                                        lastMediaSequence = segmentSequence;
                                    }

                                    if (processedSegments.Count > maxProcessedSegments)
                                    {
                                        processedSegments.Remove(processedSegments.First());
                                    }
                                }
                            }
                            segmentSequence++;
                        }
                    }

                    if (segmentsSent > 0)
                    {
                        SignalActivity();
                        await Task.Delay(TimeSpan.FromSeconds(targetDuration > 0 ? targetDuration - 1 : 1.0), combinedCt);
                    }
                    else
                    {
                        await Task.Delay(500, combinedCt);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    completionError = ex;
                    _logger.LogError(ex, "Error in HLS segment producer.");
                    SignalActivity();
                    await Task.Delay(2000, combinedCt);
                }
            }
        }
        finally
        {
            _fanout.CompleteAll(completionError);

            lock (_producerLock)
            {
                _producerStopCts?.Dispose();
                _producerStopCts = null;
                _producerTask = null;
            }

            _logger.LogInformation("Shared HLS segment producer has stopped.");
            SignalActivity();
            combinedCts.Dispose();
        }
    }

    private void StopProducerIfIdle()
    {
        lock (_producerLock)
        {
            if (_fanout.HasSubscribers)
            {
                return;
            }

            _producerStopCts?.Cancel();
        }
    }

    internal async Task<byte[]?> FetchAndDecryptSegment(
        string channelId,
        Func<Task<ChannelItemData>> channelProvider,
        string version,
        string segmentName,
        byte[]? currentKey,
        byte[]? currentIV,
        long segmentSequence,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var currentChannelData = await channelProvider();
                using var segStream = await _player.GetSegment(currentChannelData.Entity!.Id!, version, segmentName, null);
                using var ms = new MemoryStream();
                await segStream.CopyToAsync(ms, cancellationToken);

                if (currentKey is not null)
                {
                    var cipher = ms.ToArray();
                    var iv = currentIV ?? HlsEncryptionService.BuildIVFromSequence(segmentSequence);
                    return HlsEncryptionService.DecryptAes128Cbc(cipher, currentKey, iv);
                }

                return ms.ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SegmentNotFoundException seg)
            {
                // A 404 means the playlist is stale. Do not retry the same segment;
                // trigger a (debounced) playlist refresh and let the producer restart
                // against the fresh segment list.
                _logger.LogDebug(
                    "Segment {SegmentName}({url}) not found (404)",
                    segmentName,
                    seg.Url);

                _logger.LogWarning(
                    "Segment {SegmentName} not found (404) for channel {ChannelId}; refreshing playlist.",
                    segmentName,
                    channelId);

                _player.RequestPlaylistRefresh($"segment {segmentName} 404");
                return null;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var backoff = TimeSpan.FromMilliseconds(150 * attempt);
                _logger.LogWarning(ex,
                    "Retrying segment {SegmentName} for channel {ChannelId} (attempt {Attempt}/{MaxAttempts}) after transient fetch/decrypt error.",
                    segmentName,
                    channelId,
                    attempt,
                    maxAttempts);

                await Task.Delay(backoff, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to fetch/decrypt segment {SegmentName} for channel {ChannelId} after {MaxAttempts} attempts.",
                    segmentName,
                    channelId,
                    maxAttempts);
                return null;
            }
        }

        return null;
    }

    private async Task<(byte[]? key, byte[]? iv)> ParseEncryptionKeyAsync(string keyLine)
    {
        byte[]? key = null;
        byte[]? iv = null;

        if (!keyLine.Contains("METHOD=AES-128", StringComparison.OrdinalIgnoreCase))
            return (key, iv);

        var m = HlsEncryptionService.GuidPattern.Matches(keyLine);
        if (m.Count > 0)
        {
            key = await _player.GetDecryptionKey(m[^1].Value);
        }

        var ivIdx = keyLine.IndexOf("IV=0x", StringComparison.OrdinalIgnoreCase);
        if (ivIdx >= 0)
        {
            var hex = keyLine[(ivIdx + 5)..];
            var comma = hex.IndexOf(',');
            if (comma >= 0)
                hex = hex[..comma];
            iv = HlsEncryptionService.HexToBytes(hex);
        }

        return (key, iv);
    }
}
