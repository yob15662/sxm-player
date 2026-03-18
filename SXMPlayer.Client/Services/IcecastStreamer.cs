using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SXMPlayer;

/// <summary>
/// Orchestrates HLS to Icecast stream conversion with ICY metadata support.
/// Coordinates between segment production, metadata generation, and stream writing.
/// </summary>
public class IcecastStreamer
{
    private readonly ILogger<SiriusXMPlayer> _logger;
    private readonly MetadataService _metadataService;
    private readonly HlsSegmentProducer _segmentProducer;
    private readonly IcyMetadataBuilder _metadataBuilder;
    private readonly IcyStreamWriter _streamWriter;

    /// <summary>
    /// Gets or sets the ICY metadata injection interval in bytes.
    /// </summary>
    public int? ICYMetaInt
    {
        get => _metadataBuilder.MetadataInterval;
        set => _metadataBuilder.MetadataInterval = value ?? 8162;
    }

    /// <summary>
    /// Gets or sets the maximum size (in bytes) of each HTTP write to the response body.
    /// This encourages the server to emit reasonably sized HTTP chunks.
    /// </summary>
    public int OutputChunkSize
    {
        get => _streamWriter.OutputChunkSize;
        set => _streamWriter.OutputChunkSize = value;
    }

    public IcecastStreamer(ILogger<SiriusXMPlayer> logger, MetadataService metadataService, SiriusXMPlayer player)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _metadataBuilder = new IcyMetadataBuilder();
        _segmentProducer = new HlsSegmentProducer(player ?? throw new ArgumentNullException(nameof(player)), logger);
        _streamWriter = new IcyStreamWriter(_metadataBuilder, logger, _metadataService);
    }

    /// <summary>
    /// Starts producing segments from a playlist for the given channel.
    /// </summary>
    public void StartHLSReader(
        ChannelWriter<SegmentWorkItem> writer,
        Func<Task<ChannelItemData?>> channelIdProvider,
        SXMListener listener,
        CancellationToken channelChangedCt,
        CancellationToken clientDisconnectCt)
    {
        var wasAlreadyActive = _segmentProducer.StartProducer(writer, channelIdProvider, listener, channelChangedCt, clientDisconnectCt);

        if (wasAlreadyActive)
        {
            _logger.LogInformation($"Attached client {listener.IPAddress} to the active HLS segment producer.");
        }
    }

    /// <summary>
    /// Writes audio data to the HTTP response with optional ICY metadata injection.
    /// When metadata injection is enabled, metadata is injected only at AAC frame boundaries
    /// to prevent corrupting the audio stream.
    /// </summary>
    public async Task<int> WriteWithIcyAsync(
        ReadOnlyMemory<byte> data,
        HttpContext ctx,
        bool injectMeta,
        int metaInt,
        int bytesUntilMeta,
        CancellationToken ct)
    {
        return await _streamWriter.WriteAsync(data, ctx, injectMeta, metaInt, bytesUntilMeta, ct);
    }

    /// <summary>
    /// Stream-based overload for backwards compatibility.
    /// Reads entire stream into memory for frame boundary detection.
    /// </summary>
    public async Task<int> WriteWithIcyAsync(
        System.IO.Stream source,
        HttpContext ctx,
        bool injectMeta,
        int metaInt,
        int bytesUntilMeta,
        CancellationToken ct)
    {
        using var ms = new System.IO.MemoryStream();
        await source.CopyToAsync(ms, ct);
        ms.Position = 0;

        return await WriteWithIcyAsync(ms.ToArray().AsMemory(), ctx, injectMeta, metaInt, bytesUntilMeta, ct);
    }

    /// <summary>
    /// Cancels producers for inactive clients.
    /// </summary>
    public void CancelProducersForInactiveClients(System.Collections.Generic.IEnumerable<SXMListener> inactiveClients)
    {
        _segmentProducer.CancelProducersForInactiveClients(inactiveClients);
    }

    /// <summary>
    /// Clears cached metadata state to force refresh on next metadata block.
    /// </summary>
    public void ClearMetadataState()
    {
        _metadataBuilder.ClearState();
    }

    /// <summary>
    /// Builds an ICY metadata block from current now-playing information.
    /// </summary>
    public byte[] GetMetadataBlock()
    {
        return _metadataBuilder.BuildMetadataBlock(_metadataService.GetNowPlaying());
    }
}
