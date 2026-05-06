using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
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
    private const int MaxWriteAttempts = 2;

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
            await WriteToResponseWithRetryAsync(context, audioData.Slice(offset, chunk), cancellationToken);
            offset += chunk;
            remaining -= chunk;
        }

        await FlushResponseWithDisconnectHandlingAsync(context, cancellationToken);
        return int.MaxValue; // No metadata tracking needed
    }

    /// <summary>
    /// Writes audio data with ICY metadata injection.
    /// ICY metadata must be emitted at exact byte intervals, otherwise ICY clients will
    /// mis-parse the stream and audio will be corrupted.
    /// </summary>
    private async Task<int> WriteWithFrameAwareMetadataAsync(
        ReadOnlyMemory<byte> audioData,
        HttpContext context,
        int metadataInterval,
        int bytesUntilNextMetadata,
        CancellationToken cancellationToken)
    {
        if (metadataInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadataInterval), "Metadata interval must be greater than zero.");
        }

        // Keep 0 as-is so metadata is emitted immediately when the previous write ended exactly on a boundary.
        if (bytesUntilNextMetadata < 0 || bytesUntilNextMetadata > metadataInterval)
        {
            bytesUntilNextMetadata = metadataInterval;
        }

        int audioOffset = 0;
        int audioRemaining = audioData.Length;

        if (audioRemaining > 0 && !AacFrameAnalyzer.IsValidAdtsHeader(audioData.Span))
        {
            int firstBoundary = AacFrameAnalyzer.FindNextFrameBoundary(audioData.Span, maxSearch: Math.Min(audioRemaining, 4096));
            if (firstBoundary > 0 && firstBoundary < audioRemaining)
            {
                _logger.LogDebug("Skipping {SkippedBytes} leading bytes before first ADTS frame boundary.", firstBoundary);
                audioOffset += firstBoundary;
                audioRemaining -= firstBoundary;
            }
            else if (firstBoundary >= audioRemaining)
            {
                await FlushResponseWithDisconnectHandlingAsync(context, cancellationToken);
                return bytesUntilNextMetadata;
            }
        }

        while (audioRemaining > 0)
        {
            if (bytesUntilNextMetadata == 0)
            {
                var meta = _metadataBuilder.BuildMetadataBlock(_metadataService?.GetNowPlaying());
                await WriteToResponseWithRetryAsync(context, meta.AsMemory(), cancellationToken);
                bytesUntilNextMetadata = metadataInterval;
                continue;
            }

            int chunk = Math.Min(audioRemaining, Math.Min(bytesUntilNextMetadata, OutputChunkSize));
            await WriteToResponseWithRetryAsync(context, audioData.Slice(audioOffset, chunk), cancellationToken);

            audioOffset += chunk;
            audioRemaining -= chunk;
            bytesUntilNextMetadata -= chunk;
        }

        await FlushResponseWithDisconnectHandlingAsync(context, cancellationToken);
        return bytesUntilNextMetadata;
    }

    private async Task WriteToResponseWithRetryAsync(HttpContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                await context.Response.Body.WriteAsync(data, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsDisconnectException(ex))
            {
                throw CreateDisconnectCanceledException(cancellationToken, ex);
            }
            catch (Exception ex) when (attempt < MaxWriteAttempts)
            {
                _logger.LogDebug(ex, "Transient response write failure; retrying attempt {Attempt}/{MaxAttempts}.", attempt, MaxWriteAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task FlushResponseWithDisconnectHandlingAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (IsDisconnectException(ex))
        {
            throw CreateDisconnectCanceledException(cancellationToken, ex);
        }
    }

    private static OperationCanceledException CreateDisconnectCanceledException(CancellationToken cancellationToken, Exception inner)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(cancellationToken);
        }

        return new OperationCanceledException("Client disconnected while writing Icecast response.", inner, cancellationToken);
    }

    private static bool IsDisconnectException(Exception ex)
    {
        if (ex is IOException)
        {
            return true;
        }

        if (ex is ConnectionAbortedException)
        {
            return true;
        }

        if (ex is ConnectionResetException)
        {
            return true;
        }

        if (ex is SocketException)
        {
            return true;
        }

        if (ex.InnerException is not null)
        {
            return IsDisconnectException(ex.InnerException);
        }

        return false;
    }
}
