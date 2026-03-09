using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SXMPlayer;

/// <summary>
/// Writes audio data to HTTP response with optional ICY metadata injection
/// that respects AAC frame boundaries to maintain audio integrity.
/// </summary>
public class IcyStreamWriter
{
    private readonly IcyMetadataBuilder _metadataBuilder;
    private readonly ILogger _logger;
    private readonly MetadataService? _metadataService;

    /// <summary>
    /// Gets or sets the maximum size (in bytes) of each HTTP write to the response body.
    /// This encourages the server to emit reasonably sized HTTP chunks for clients that rely on chunked transfer.
    /// </summary>
    public int OutputChunkSize { get; set; } = 16 * 1024; // 16 KiB default

    public IcyStreamWriter(IcyMetadataBuilder metadataBuilder, ILogger logger, MetadataService? metadataService = null)
    {
        _metadataBuilder = metadataBuilder ?? throw new ArgumentNullException(nameof(metadataBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataService = metadataService;
    }

    /// <summary>
    /// Writes audio data to the HTTP response with optional ICY metadata injection.
    /// When metadata injection is enabled, metadata is injected only at AAC frame boundaries
    /// to prevent corrupting the audio stream.
    /// </summary>
    /// <param name="audioData">The audio data to write</param>
    /// <param name="context">The HTTP context for the response</param>
    /// <param name="injectMetadata">Whether to inject ICY metadata</param>
    /// <param name="metadataInterval">Interval in bytes between metadata blocks</param>
    /// <param name="bytesUntilNextMetadata">Current position in the metadata interval</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated position in metadata interval after writing</returns>
    public async Task<int> WriteAsync(
        ReadOnlyMemory<byte> audioData,
        HttpContext context,
        bool injectMetadata,
        int metadataInterval,
        int bytesUntilNextMetadata,
        CancellationToken cancellationToken)
    {
        if (!injectMetadata)
        {
            return await WriteWithoutMetadataAsync(audioData, context, cancellationToken);
        }

        return await WriteWithFrameAwareMetadataAsync(
            audioData, context, metadataInterval, bytesUntilNextMetadata, cancellationToken);
    }

    /// <summary>
    /// Writes audio data without metadata injection.
    /// </summary>
    private async Task<int> WriteWithoutMetadataAsync(
        ReadOnlyMemory<byte> audioData,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        int remaining = audioData.Length;

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, OutputChunkSize);
            await context.Response.Body.WriteAsync(audioData.Slice(offset, chunk), cancellationToken);
            offset += chunk;
            remaining -= chunk;
        }

        await context.Response.Body.FlushAsync(cancellationToken);
        return int.MaxValue; // No metadata tracking needed
    }

    /// <summary>
    /// Writes audio data with ICY metadata injection at AAC frame boundaries.
    /// </summary>
    private async Task<int> WriteWithFrameAwareMetadataAsync(
        ReadOnlyMemory<byte> audioData,
        HttpContext context,
        int metadataInterval,
        int bytesUntilNextMetadata,
        CancellationToken cancellationToken)
    {
        int audioOffset = 0;
        int audioRemaining = audioData.Length;

        while (audioRemaining > 0)
        {
            // Inject metadata if due
            if (bytesUntilNextMetadata <= 0)
            {
                var meta = _metadataBuilder.BuildMetadataBlock(_metadataService?.GetNowPlaying());
                await context.Response.Body.WriteAsync(meta, 0, meta.Length, cancellationToken);
                bytesUntilNextMetadata = metadataInterval;
            }

            // Get span for current region (zero-copy view)
            var audioSpan = audioData.Slice(audioOffset, audioRemaining).Span;

            // Find first valid frame
            int firstFrameOffset = AacFrameAnalyzer.FindNextFrameBoundary(audioSpan);
            
            if (firstFrameOffset >= audioSpan.Length)
            {
                // No valid frame found in remaining data, skip it all
                _logger.LogWarning("No valid AAC frame found in remaining {Bytes} bytes, skipping", audioRemaining);
                break;
            }

            if (firstFrameOffset > 0)
            {
                // Skip junk data before first valid frame
                _logger.LogDebug("Skipping {Bytes} bytes of non-frame data to align to AAC boundary", firstFrameOffset);
                audioOffset += firstFrameOffset;
                audioRemaining -= firstFrameOffset;
                continue;
            }

            // We're at a frame boundary (firstFrameOffset == 0)
            // Verify the frame at position 0 is actually valid
            int firstFrameSize = AacFrameAnalyzer.TryDetectFrameSize(audioSpan);
            if (firstFrameSize <= 0)
            {
                // False positive sync marker - skip past it and keep searching
                _logger.LogDebug("Invalid frame at position 0 despite sync marker, skipping 1 byte");
                audioOffset += 1;
                audioRemaining -= 1;
                continue;
            }

            // Calculate how many complete frames we can write before metadata
            int bytesToWrite = 0;
            int currentPos = 0;

            while (currentPos < audioRemaining && bytesToWrite < bytesUntilNextMetadata)
            {
                int frameSize = AacFrameAnalyzer.TryDetectFrameSize(audioSpan.Slice(currentPos));
                if (frameSize <= 0)
                {
                    // Invalid frame, stop here
                    break;
                }

                // Check if this frame fits before metadata boundary
                if (bytesToWrite + frameSize <= bytesUntilNextMetadata && currentPos + frameSize <= audioRemaining)
                {
                    bytesToWrite += frameSize;
                    currentPos += frameSize;
                }
                else
                {
                    // Frame would cross metadata boundary, stop here
                    break;
                }
            }

            if (bytesToWrite == 0)
            {
                // Can't fit any complete frame before metadata boundary
                // If the frame is larger than the metadata interval, we'll never be able to write it
                // Skip this frame and continue
                if (firstFrameSize > metadataInterval)
                {
                    _logger.LogWarning("Frame size {FrameSize} exceeds metadata interval {MetadataInterval}, skipping frame", firstFrameSize, metadataInterval);
                    audioOffset += firstFrameSize;
                    audioRemaining -= firstFrameSize;
                    continue;
                }
                
                // Otherwise, inject metadata now and try again with fresh interval
                var meta = _metadataBuilder.BuildMetadataBlock(_metadataService?.GetNowPlaying());
                await context.Response.Body.WriteAsync(meta, 0, meta.Length, cancellationToken);
                bytesUntilNextMetadata = metadataInterval;
                continue;
            }

            // Write complete frames
            int written = 0;
            while (written < bytesToWrite)
            {
                int chunk = Math.Min(bytesToWrite - written, OutputChunkSize);
                await context.Response.Body.WriteAsync(
                    audioData.Slice(audioOffset + written, chunk), cancellationToken);
                written += chunk;
                bytesUntilNextMetadata -= chunk;
            }

            audioOffset += bytesToWrite;
            audioRemaining -= bytesToWrite;
        }

        await context.Response.Body.FlushAsync(cancellationToken);
        return bytesUntilNextMetadata;
    }
}
