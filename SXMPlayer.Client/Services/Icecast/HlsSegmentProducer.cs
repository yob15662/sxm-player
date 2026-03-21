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
    /// <returns>True when the shared producer was already active.</returns>
    public bool StartProducer(
        ChannelWriter<SegmentWorkItem> writer,
        Func<Task<ChannelItemData?>> channelProvider,
        SXMListener listener,
        CancellationToken channelChangedCt,
        CancellationToken clientDisconnectToken)
    {
        lock (_producerLock)
        {
            var wasAlreadyActive = _producerTask is { IsCompleted: false };

            _fanout.Register(listener, writer, clientDisconnectToken);

            if (!wasAlreadyActive)
            {
                _logger.LogInformation("Starting HLS segment producer.");
                _producerStopCts?.Dispose();
                _producerStopCts = new CancellationTokenSource();
                var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(channelChangedCt, _producerStopCts.Token);
                _producerTask = RunProducerAsync(channelProvider, combinedCts);
            }

            return wasAlreadyActive;
        }
    }

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
                                        currentKey, currentIV, segmentSequence);

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

    private async Task<byte[]?> FetchAndDecryptSegment(
        string channelId,
        Func<Task<ChannelItemData>> channelProvider,
        string version,
        string segmentName,
        byte[]? currentKey,
        byte[]? currentIV,
        long segmentSequence)
    {
        try
        {
            var currentChannelData = await channelProvider();
            using var segStream = await _player.GetSegment(currentChannelData.Entity!.Id!, version, segmentName, null);

            if (currentKey is not null)
            {
                using var ms = new MemoryStream();
                await segStream.CopyToAsync(ms);
                var cipher = ms.ToArray();
                var iv = currentIV ?? HlsEncryptionService.BuildIVFromSequence(segmentSequence);
                return HlsEncryptionService.DecryptAes128Cbc(cipher, currentKey, iv);
            }
            else
            {
                using var ms = new MemoryStream();
                await segStream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching/decrypting segment {segmentName}");
            return null;
        }
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
