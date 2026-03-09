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

            // Get current position data
            var currentData = audioData.Slice(audioOffset, audioRemaining);
            var currentSpan = currentData.Span;

            // Try to find the next valid frame boundary
            int frameOffset = AacFrameAnalyzer.FindNextFrameBoundary(currentSpan);
            
            if (frameOffset >= currentSpan.Length)
            {
                // No valid frame found in remaining data
                // Rather than lose data, write what we have without frame alignment
                if (audioRemaining > 0)
                {
                    _logger.LogWarning("No valid AAC frame found in remaining {Bytes} bytes, writing as-is", audioRemaining);
                    
                    int written = 0;
                    while (written < audioRemaining)
                    {
                        int chunk = Math.Min(audioRemaining - written, OutputChunkSize);
                        await context.Response.Body.WriteAsync(
                            audioData.Slice(audioOffset + written, chunk), cancellationToken);
                        written += chunk;
                        bytesUntilNextMetadata -= chunk;
                    }
                    
                    audioOffset += audioRemaining;
                    audioRemaining = 0;
                }
                break;
            }

            // If there's junk data before the frame, validate that skipping is safe
            if (frameOffset > 0)
            {
                // Before skipping junk, verify there's actually a valid frame after it
                // and that we're not in the middle of a frame sequence
                var afterJunk = currentSpan.Slice(frameOffset);
                int frameSize = AacFrameAnalyzer.TryDetectFrameSize(afterJunk);
                
                if (frameSize > 0 && frameOffset + frameSize <= audioRemaining)
                {
                    // Safe to skip: we have confirmed a valid frame after the junk
                    _logger.LogDebug("Skipping {Bytes} bytes of non-frame data to align to AAC boundary", frameOffset);
                    audioOffset += frameOffset;
                    audioRemaining -= frameOffset;
                    continue;
                }
                else
                {
                    // Uncertain about frame validity; write junk data as-is rather than skip
                    _logger.LogWarning("Uncertain frame after junk data; writing {Bytes} bytes without frame alignment", frameOffset);
                    await context.Response.Body.WriteAsync(audioData.Slice(audioOffset, frameOffset), cancellationToken);
                    audioOffset += frameOffset;
                    audioRemaining -= frameOffset;
                    bytesUntilNextMetadata -= frameOffset;
                    continue;
                }
            }

            // We're at a valid frame boundary (frameOffset == 0)
            // Carefully collect consecutive valid frames
            int framesTotalSize = 0;
            int checkPos = 0;

            while (checkPos < audioRemaining)
            {
                int frameSize = AacFrameAnalyzer.TryDetectFrameSize(currentSpan.Slice(checkPos));
                
                if (frameSize <= 0)
                {
                    // No more valid frames from this point
                    break;
                }

                // Check if adding this frame would exceed metadata boundary
                if (framesTotalSize + frameSize > bytesUntilNextMetadata)
                {
                    // Frame would cross metadata boundary, stop here
                    break;
                }

                // Check if we have enough data for this frame
                if (checkPos + frameSize > audioRemaining)
                {
                    // Incomplete frame at end of buffer, don't include it
                    // It will be processed in the next call with more data
                    break;
                }

                framesTotalSize += frameSize;
                checkPos += frameSize;
            }

            if (framesTotalSize == 0)
            {
                // Can't fit any complete frame before metadata boundary
                int firstFrameSize = AacFrameAnalyzer.TryDetectFrameSize(currentSpan);
                
                if (firstFrameSize <= 0)
                {
                    // Frame detection failed - write data as-is without frame alignment
                    _logger.LogWarning("Invalid frame detected at position {Offset}; writing {Bytes} bytes without frame alignment", audioOffset, Math.Min(OutputChunkSize, audioRemaining));
                    int chunk = Math.Min(OutputChunkSize, audioRemaining);
                    await context.Response.Body.WriteAsync(
                        audioData.Slice(audioOffset, chunk), cancellationToken);
                    audioOffset += chunk;
                    audioRemaining -= chunk;
                    bytesUntilNextMetadata -= chunk;
                    continue;
                }
                
                if (firstFrameSize > metadataInterval)
                {
                    _logger.LogWarning("Frame size {FrameSize} exceeds metadata interval {MetadataInterval}", firstFrameSize, metadataInterval);
                    
                    // Write the oversized frame without breaking it up with metadata
                    int written = 0;
                    while (written < firstFrameSize && audioOffset + written < audioData.Length)
                    {
                        int chunk = Math.Min(firstFrameSize - written, OutputChunkSize);
                        await context.Response.Body.WriteAsync(
                            audioData.Slice(audioOffset + written, chunk), cancellationToken);
                        written += chunk;
                        bytesUntilNextMetadata -= chunk;
                    }
                    
                    audioOffset += written;
                    audioRemaining -= written;
                    continue;
                }
                
                // Inject metadata and try again
                var meta = _metadataBuilder.BuildMetadataBlock(_metadataService?.GetNowPlaying());
                await context.Response.Body.WriteAsync(meta, 0, meta.Length, cancellationToken);
                bytesUntilNextMetadata = metadataInterval;
                continue;
            }

            // Write the complete frames we collected
            int totalWritten = 0;
            while (totalWritten < framesTotalSize)
            {
                int chunk = Math.Min(framesTotalSize - totalWritten, OutputChunkSize);
                await context.Response.Body.WriteAsync(
                    audioData.Slice(audioOffset + totalWritten, chunk), cancellationToken);
                totalWritten += chunk;
                bytesUntilNextMetadata -= chunk;
            }

            audioOffset += framesTotalSize;
            audioRemaining -= framesTotalSize;
        }

        await context.Response.Body.FlushAsync(cancellationToken);
        return bytesUntilNextMetadata;
    }
}
