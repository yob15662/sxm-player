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
    private readonly Func<NowPlayingData?>? _nowPlayingProvider;

    /// <summary>
    /// Gets or sets the maximum size (in bytes) of each HTTP write to the response body.
    /// This encourages the server to emit reasonably sized HTTP chunks for clients that rely on chunked transfer.
    /// </summary>
    public int OutputChunkSize { get; set; } = 16 * 1024; // 16 KiB default

    public IcyStreamWriter(IcyMetadataBuilder metadataBuilder, ILogger logger, Func<NowPlayingData?>? nowPlayingProvider = null)
    {
        _metadataBuilder = metadataBuilder ?? throw new ArgumentNullException(nameof(metadataBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nowPlayingProvider = nowPlayingProvider;
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
                var meta = _metadataBuilder.BuildMetadataBlock(_nowPlayingProvider?.Invoke());
                await context.Response.Body.WriteAsync(meta, 0, meta.Length, cancellationToken);
                bytesUntilNextMetadata = metadataInterval;
            }

            // Get span for current region (zero-copy view)
            var audioSpan = audioData.Slice(audioOffset, audioRemaining).Span;

            // Find next frame boundary within budget
            int bytesAvailable = Math.Min(audioRemaining, bytesUntilNextMetadata);
            int searchLimit = Math.Min(bytesAvailable, 192 * 1024);

            int frameMarkerPos = AacFrameAnalyzer.FindNextFrameBoundary(audioSpan.Slice(0, searchLimit));
            int nextFrameBoundary = 0;

            if (frameMarkerPos < searchLimit)
            {
                int frameSize = AacFrameAnalyzer.TryDetectFrameSize(audioSpan.Slice(frameMarkerPos));
                if (frameSize > 0)
                {
                    nextFrameBoundary = frameMarkerPos + frameSize;
                }
            }

            // Write to frame boundary if found, otherwise write up to budget
            int toWrite = (nextFrameBoundary > 0 && nextFrameBoundary <= bytesAvailable)
                ? nextFrameBoundary
                : Math.Min(audioRemaining, bytesUntilNextMetadata);

            int written = 0;
            while (written < toWrite)
            {
                int chunk = Math.Min(toWrite - written, OutputChunkSize);
                await context.Response.Body.WriteAsync(
                    audioData.Slice(audioOffset + written, chunk), cancellationToken);
                written += chunk;
                bytesUntilNextMetadata -= chunk;
            }

            audioOffset += toWrite;
            audioRemaining -= toWrite;
        }

        await context.Response.Body.FlushAsync(cancellationToken);
        return bytesUntilNextMetadata;
    }
}
