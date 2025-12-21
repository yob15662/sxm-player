using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SXMPlayer;

/// <summary>
/// Streams AAC audio as Icecast/Shoutcast with optional ICY metadata injection.
/// Delegates decryption to HlsEncryptionService and segment retrieval to callbacks.
/// Also handles ICY metadata generation and stream-wrapping.
/// </summary>
public class IcecastStreamer
{
    private readonly ILogger<SiriusXMPlayer> _logger;
    private readonly SiriusXMPlayer _player;

    // Track active producers per client to allow cancellation
    private readonly ConcurrentDictionary<SXMListener, CancellationTokenSource> _activeProducers = new();

    public int? ICYMetaInt { get; set; }

    /// <summary>
    /// Maximum size (in bytes) of each HTTP write to the response body. This encourages
    /// the server to emit reasonably sized HTTP chunks for clients that rely on chunked transfer.
    /// </summary>
    public int OutputChunkSize { get; set; } = 16 * 1024; // 16 KiB default

    // ICY metadata state
    private string? _lastStreamTitle;

    public IcecastStreamer(ILogger<SiriusXMPlayer> logger, SiriusXMPlayer player)
    {
        _logger = logger;
        _player = player;
    }

    public record SegmentWorkItem(string SegmentName, string Version, long MediaSequence, byte[]? Key, byte[]? IV);

    /// <summary>
    /// Cancels playlist producers for clients that are marked as inactive.
    /// </summary>
    /// <param name="inactiveClients">List of clients that are no longer active</param>
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

    public Task StartPlaylistProducer(ChannelWriter<SegmentWorkItem> writer, Func<Task<ChannelItemData>> channelIdProvider, SXMListener listener, CancellationToken playlistRefreshCt, CancellationToken clientDisconnectCt)
    {
        _logger.LogInformation("Starting playlist producer.");

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
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(playlistRefreshCt, clientDisconnectCt, producerCts.Token);

        return Task.Run(async () =>
     {
         var combinedCt = combinedCts.Token;
         long lastMediaSequence = -1;
         var processedSegments = new ConcurrentDictionary<string, bool>();
         const int maxProcessedSegments = 20; // Avoid re-queueing recent segments
         string? lastChannelId = null;
         bool useCache = true;

         try
         {
             while (!combinedCt.IsCancellationRequested)
             {
                 try
                 {
                     string channelId = (await channelIdProvider() ?? throw new InvalidOperationException("No current channel")).Entity!.Id!;

                     if (lastChannelId != channelId)
                     {
                         if (lastChannelId is not null)
                         {
                             _logger.LogInformation($"Playlist producer switching from channel '{lastChannelId}' to '{channelId}'.");
                         }
                         lastChannelId = channelId;
                         lastMediaSequence = -1; // Reset sequence to fetch latest segment
                         processedSegments.Clear();
                     }

                     var playlist = await _player.GetStreamPlaylist(channelId, listener, alias: channelId, useCache: useCache);
                     useCache = false; // Only use cache on first attempt
                     if (string.IsNullOrEmpty(playlist))
                     {
                         await Task.Delay(500, combinedCt);
                         continue;
                     }

                     var lines = playlist.Split('\n');
                     byte[]? currentKey = null;
                     byte[]? currentIV = null;
                     long currentMediaSequence = -1;
                     double targetDuration = 2.0; // Default segment duration

                     // Find media sequence and target duration first
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
                         // On first run, start with the last segment in the playlist
                         lastMediaSequence = currentMediaSequence + lines.Count(s => s.Trim().EndsWith(".aac")) - 2;
                     }

                     long segmentSequence = currentMediaSequence;
                     var segmentsSent = 0;
                     foreach (var line in lines)
                     {
                         var l = line.Trim();
                         if (l.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                         {
                             if (l.Contains("METHOD=AES-128", StringComparison.OrdinalIgnoreCase))
                             {
                                 var m = HlsEncryptionService.GuidPattern.Matches(l);
                                 if (m.Count > 0) currentKey = await _player.GetDecryptionKey(m[^1].Value);

                                 var ivIdx = l.IndexOf("IV=0x", StringComparison.OrdinalIgnoreCase);
                                 if (ivIdx >= 0)
                                 {
                                     var hex = l[(ivIdx + 5)..];
                                     var comma = hex.IndexOf(',');
                                     if (comma >= 0) hex = hex[..comma];
                                     currentIV = HlsEncryptionService.HexToBytes(hex);
                                 }
                                 else
                                 {
                                     currentIV = null;
                                 }
                             }
                             else
                             {
                                 currentKey = null; currentIV = null;
                             }
                         }
                         else if (l.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
                         {
                             if (segmentSequence > lastMediaSequence)
                             {
                                 var segmentName = l.Split('/').Last();
                                 if (processedSegments.TryAdd(segmentName, true))
                                 {
                                     var parts = l.Split('/');
                                     var version = parts[^2];
                                     var item = new SegmentWorkItem(segmentName, version, segmentSequence, currentKey, currentIV);
                                     await writer.WriteAsync(item, combinedCt);
                                     if (listener is not null)
                                     {
                                         listener.LastActivity = DateTimeOffset.Now;
                                     }
                                     segmentsSent++;
                                     lastMediaSequence = segmentSequence;

                                     if (processedSegments.Count > maxProcessedSegments)
                                     {
                                         processedSegments.TryRemove(processedSegments.Keys.First(), out _);
                                     }
                                 }
                             }
                             segmentSequence++;
                         }
                     }
                     // Wait for approx one segment duration before fetching next playlist
                     await Task.Delay(TimeSpan.FromSeconds((targetDuration > 0 ? targetDuration - 1 : 2.0) * (Math.Max(1, segmentsSent - 2))), combinedCt);
                 }
                 catch (OperationCanceledException)
                 {
                     break; // Exit loop if cancellation is requested
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Error in playlist producer.");
                     await Task.Delay(2000, combinedCt); // Wait a bit longer on error
                 }
             }
         }
         finally
         {
             // Clean up this producer from the active producers dictionary
             _activeProducers.TryRemove(listener, out _);
             _logger.LogInformation($"Playlist producer for client {listener.IPAddress} has stopped.");
             combinedCts.Dispose();
         }
     }, clientDisconnectCt).ContinueWith(t => writer.Complete(t.Exception?.GetBaseException()), TaskContinuationOptions.None);
    }

    public async Task<int> WriteWithIcyAsync(Stream source, HttpContext ctx, bool injectMeta, int metaInt, int bytesUntilMeta, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                int offset = 0;
                while (read > 0)
                {
                    if (injectMeta && bytesUntilMeta == 0)
                    {
                        var meta = GetMetadataBlock();
                        await ctx.Response.Body.WriteAsync(meta, 0, meta.Length, ct);
                        bytesUntilMeta = metaInt;
                    }

                    // Limit this write to the smaller of remaining data or remaining bytes until next metadata block
                    int writeBudget = injectMeta ? Math.Min(read, bytesUntilMeta) : read;

                    // Further split into fixed-size output chunks to encourage HTTP chunked framing
                    while (writeBudget > 0)
                    {
                        int chunk = Math.Min(writeBudget, OutputChunkSize);
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(offset, chunk), ct);
                        offset += chunk;
                        read -= chunk;
                        writeBudget -= chunk;
                        if (injectMeta)
                        {
                            bytesUntilMeta -= chunk;
                        }
                    }
                }
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return bytesUntilMeta;
    }

    public async Task<int> WriteWithIcyAsync(byte[] data, HttpContext ctx, bool injectMeta, int metaInt, int bytesUntilMeta, CancellationToken ct)
    {
        int offset = 0;
        int remaining = data.Length;
        while (remaining > 0)
        {
            if (injectMeta && bytesUntilMeta == 0)
            {
                var meta = GetMetadataBlock();
                await ctx.Response.Body.WriteAsync(meta, 0, meta.Length, ct);
                bytesUntilMeta = metaInt;
            }

            // Limit this write to the smaller of remaining data or remaining bytes until next metadata block
            int writeBudget = injectMeta ? Math.Min(remaining, bytesUntilMeta) : remaining;

            // Further split into fixed-size output chunks to encourage HTTP chunked framing
            while (writeBudget > 0)
            {
                int chunk = Math.Min(writeBudget, OutputChunkSize);
                await ctx.Response.Body.WriteAsync(data.AsMemory(offset, chunk), ct);
                offset += chunk;
                remaining -= chunk;
                writeBudget -= chunk;
                if (injectMeta)
                {
                    bytesUntilMeta -= chunk;
                }
            }
        }
        await ctx.Response.Body.FlushAsync(ct);
        return bytesUntilMeta;
    }

    internal void ClearMetadataState()
    {
        _lastStreamTitle = null;
    }

    /// <summary>
    /// Creates a stream wrapper that injects ICY metadata at regular intervals without buffering the entire source.
    /// </summary>
    /// <param name="audioStream">The original audio stream.</param>
    /// <param name="icyMetaInt">Interval in bytes after which to inject an ICY metadata block.</param>
    /// <returns>A non-seekable stream that injects ICY metadata during reads.</returns>
    public Task<Stream> CreateICYStream(Stream audioStream, int icyMetaInt)
    {
        _lastStreamTitle = null;
        if (audioStream is null) throw new ArgumentNullException(nameof(audioStream));
        if (!audioStream.CanRead) throw new ArgumentException("Source stream must be readable", nameof(audioStream));
        if (icyMetaInt <= 0) throw new ArgumentOutOfRangeException(nameof(icyMetaInt));

        // Wrap the source stream; disposing the wrapper will dispose the source stream.
        Stream wrapper = new IcyInjectingStream(audioStream, icyMetaInt, () => GetMetadataBlock());
        return Task.FromResult(wrapper);
    }

    private DateTime _lastMetadataUpdate = DateTime.MinValue;

    /// <summary>
    /// Builds the ICY metadata block with current now playing information.
    /// ICY metadata format: length byte (in 16-byte blocks) + padded metadata string
    /// </summary>
    /// <returns>Byte array containing the ICY metadata block</returns>
    public byte[] GetMetadataBlock()
    {
        string metadataString = "";

        var nowPlaying = _player.GetNowPlaying();
        if (nowPlaying is not null)
        {
            var streamTitle = $"{nowPlaying.artist} - {nowPlaying.song}";
            if (streamTitle == _lastStreamTitle && DateTime.UtcNow <= _lastMetadataUpdate.AddSeconds(10))
            {
                // No change in title, send empty metadata to indicate no change   
                return new byte[] { 0 };
            }
            streamTitle = streamTitle.Replace("'", ""); // Remove single quotes to avoid breaking the format
            metadataString = $"StreamTitle='{streamTitle}';";
            _lastStreamTitle = streamTitle;
            _lastMetadataUpdate = DateTime.UtcNow;
        }
        else
        {
            // No now playing info, send empty metadata
            _lastStreamTitle = null;
            return new byte[] { 0 };
        }

        // ICY metadata format: length byte (in 16-byte blocks) + padded metadata
        var metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadataString);
        var metadataLength = (metadataBytes.Length + 15) / 16; // Round up to 16-byte blocks
        var paddedLength = metadataLength * 16;

        var result = new byte[paddedLength + 1];
        result[0] = (byte)metadataLength;

        Array.Copy(metadataBytes, 0, result, 1, metadataBytes.Length);

        return result;
    }

    /// <summary>
    /// Stream wrapper that injects ICY metadata blocks every N bytes of audio data.
    /// Non-seekable, read-only.
    /// </summary>
    private sealed class IcyInjectingStream : Stream
    {
        private readonly Stream _source;
        private readonly int _icyMetaInt;
        private readonly Func<byte[]> _metadataFactory;

        private int _bytesUntilMeta;
        private byte[]? _metadataBuffer;
        private int _metadataOffset;
        private bool _disposed;

        public IcyInjectingStream(Stream source, int icyMetaInt, Func<byte[]> metadataFactory)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (!source.CanRead) throw new ArgumentException("Source stream must be readable", nameof(source));
            if (icyMetaInt <= 0) throw new ArgumentOutOfRangeException(nameof(icyMetaInt));
            _icyMetaInt = icyMetaInt;
            _metadataFactory = metadataFactory ?? throw new ArgumentNullException(nameof(metadataFactory));
            _bytesUntilMeta = _icyMetaInt;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if ((uint)offset > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if ((uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(count));
            if (_disposed) throw new ObjectDisposedException(nameof(IcyInjectingStream));

            return ReadCoreAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IcyInjectingStream));
            if (MemoryMarshal.TryGetArray(destination, out ArraySegment<byte> seg))
            {
                return new ValueTask<int>(ReadCoreAsync(seg.Array!, seg.Offset, seg.Count, cancellationToken));
            }
            // Fallback for non-array-backed memory
            var arr = ArrayPool<byte>.Shared.Rent(destination.Length);
            return new ValueTask<int>(ReadIntoMemoryFallbackAsync(arr, destination, cancellationToken));
        }

        private async Task<int> ReadIntoMemoryFallbackAsync(byte[] temp, Memory<byte> destination, CancellationToken cancellationToken)
        {
            try
            {
                int read = await ReadCoreAsync(temp, 0, destination.Length, cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    temp.AsMemory(0, read).CopyTo(destination);
                }
                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }

        private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalWritten = 0;

            while (totalWritten < count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // If we are currently emitting a metadata block, write from it first
                if (_metadataBuffer is not null)
                {
                    int metaRemaining = _metadataBuffer.Length - _metadataOffset;
                    int toCopy = Math.Min(metaRemaining, count - totalWritten);
                    Array.Copy(_metadataBuffer, _metadataOffset, buffer, offset + totalWritten, toCopy);
                    _metadataOffset += toCopy;
                    totalWritten += toCopy;

                    if (_metadataOffset >= _metadataBuffer.Length)
                    {
                        // Finished metadata, reset state and continue with audio
                        _metadataBuffer = null;
                        _metadataOffset = 0;
                        _bytesUntilMeta = _icyMetaInt;
                    }

                    // If we filled the caller's buffer with metadata, return now
                    if (totalWritten >= count)
                    {
                        break;
                    }

                    // Otherwise continue to fetch audio bytes
                }

                // If it's time to inject new metadata and we are not already emitting one
                if (_bytesUntilMeta == 0 && _metadataBuffer is null)
                {
                    _metadataBuffer = _metadataFactory();
                    _metadataOffset = 0;
                    continue; // loop will write metadata on next iteration
                }

                // Read audio bytes up to either the requested count or until next metadata injection
                int toRead = Math.Min(_bytesUntilMeta, count - totalWritten);
                if (toRead == 0)
                {
                    // The caller requested zero additional audio bytes but we still owe a metadata block
                    // Prepare metadata for the next iteration
                    _metadataBuffer = _metadataFactory();
                    _metadataOffset = 0;
                    continue;
                }

                int read = await _source.ReadAsync(buffer.AsMemory(offset + totalWritten, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    // End of source stream
                    break;
                }

                totalWritten += read;
                _bytesUntilMeta -= read;

                if (_bytesUntilMeta == 0)
                {
                    // Next loop iteration will emit metadata
                }
            }

            return totalWritten;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _source.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
