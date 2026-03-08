using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<SXMListener, CancellationTokenSource> _activeProducers = new();

    public record SegmentWorkItem(string SegmentName, string Version, long MediaSequence, Memory<byte>? AudioData);

    public HlsSegmentProducer(SiriusXMPlayer player, ILogger logger)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts producing segments for a channel, writing them to the provided channel writer.
    /// Handles playlist fetching, decryption, and segment queuing.
    /// </summary>
    public Task StartProducer(
        ChannelWriter<SegmentWorkItem> writer,
        Func<Task<ChannelItemData>> channelProvider,
        SXMListener listener,
        CancellationToken playlistRefreshToken,
        CancellationToken clientDisconnectToken)
    {
        _logger.LogInformation("Starting HLS segment producer.");

        // Cancel any existing producer for this client
        if (_activeProducers.TryRemove(listener, out var existingCts))
        {
            _logger.LogInformation($"Cancelling previous producer for client {listener.IPAddress}");
            existingCts.Cancel();
            existingCts.Dispose();
        }

        // Create a new cancellation token source for this producer
        var producerCts = new CancellationTokenSource();
        _activeProducers[listener] = producerCts;

        // Combine all cancellation tokens
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(playlistRefreshToken, clientDisconnectToken, producerCts.Token);

        return Task.Run(async () =>
        {
            var combinedCt = combinedCts.Token;
            long lastMediaSequence = -1;
            var processedSegments = new HashSet<string>();
            const int maxProcessedSegments = 50;
            string? lastChannelId = null;
            bool useCache = true;

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

                        var playlist = await _player.GetStreamPlaylist(channelId, listener, alias: channelId, useCache: useCache);
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

                        // Parse playlist metadata
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

                        // Process segments
                        foreach (var line in lines)
                        {
                            var l = line.Trim();
                            if (l.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                            {
                                ParseEncryptionKey(l, out currentKey, out currentIV);
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
                                            listener, currentKey, currentIV, segmentSequence);

                                        if (audioData is not null)
                                        {
                                            var item = new SegmentWorkItem(segmentName, version, segmentSequence, new Memory<byte>(audioData));
                                            await writer.WriteAsync(item, combinedCt);
                                            if (listener is not null)
                                            {
                                                listener.LastActivity = DateTimeOffset.Now;
                                            }
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

                        // Wait before fetching next playlist
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
                        _logger.LogError(ex, "Error in HLS segment producer.");
                        await Task.Delay(2000, combinedCt);
                    }
                }
            }
            finally
            {
                _activeProducers.TryRemove(listener, out _);
                _logger.LogInformation($"HLS segment producer for client {listener.IPAddress} has stopped.");
                combinedCts.Dispose();
            }
        }, clientDisconnectToken).ContinueWith(t => writer.Complete(t.Exception?.GetBaseException()), TaskContinuationOptions.None);
    }

    /// <summary>
    /// Cancels producers for inactive clients.
    /// </summary>
    public void CancelProducersForInactiveClients(IEnumerable<SXMListener> inactiveClients)
    {
        foreach (var client in inactiveClients)
        {
            if (_activeProducers.TryRemove(client, out var cts))
            {
                _logger.LogInformation($"Cancelling producer for inactive client {client.IPAddress}");
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private async Task<byte[]?> FetchAndDecryptSegment(
        string channelId,
        Func<Task<ChannelItemData>> channelProvider,
        string version,
        string segmentName,
        SXMListener? listener,
        byte[]? currentKey,
        byte[]? currentIV,
        long segmentSequence)
    {
        try
        {
            var currentChannelData = await channelProvider();
            using var segStream = await _player.GetSegment(currentChannelData.Entity!.Id!, version, segmentName, listener);

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

    private void ParseEncryptionKey(string keyLine, out byte[]? key, out byte[]? iv)
    {
        key = null;
        iv = null;

        if (!keyLine.Contains("METHOD=AES-128", StringComparison.OrdinalIgnoreCase))
            return;

        var m = HlsEncryptionService.GuidPattern.Matches(keyLine);
        if (m.Count > 0)
        {
            key = _player.GetDecryptionKey(m[^1].Value).GetAwaiter().GetResult();
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
    }
}
