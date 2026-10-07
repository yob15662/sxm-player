using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Resolves tuning information for a channel. Moved verbatim from
/// <c>SiriusXMPlayer.TuneSource</c>, including the retry/relogin loop.
/// </summary>
public class StreamTuner : IStreamTuner
{
    private readonly SxmSessionService sxmSessionService;
    private readonly APISession session;
    private readonly MetadataService metadataService;
    private readonly ILogger<StreamTuner> logger;
    private readonly CancellationTokenSource tokenSource;

    public StreamTuner(
        SxmSessionService sxmSessionService,
        APISession session,
        MetadataService metadataService,
        ILogger<StreamTuner> logger,
        CancellationTokenSource tokenSource)
    {
        this.sxmSessionService = sxmSessionService ?? throw new ArgumentNullException(nameof(sxmSessionService));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
    }

    public async Task<Streams> TuneSourceAsync(string channelId, int retries = 0)
    {
        if (retries >= 5)
        {
            logger.LogError($"Too many retries");
            throw new InvalidOperationException("Too many retries");
        }
        await sxmSessionService.LoginIfNecessary(nameof(TuneSourceAsync));
        try
        {
            var allChannels = await metadataService.GetChannelsAsync();
            var channel = allChannels.SingleOrDefault(c => c.Entity.Id == channelId);
            if (channel is null)
            {
                throw new InvalidOperationException($"Channel {channelId} not found - {allChannels.Count} channels loaded");
            }
            var manifestVariant = "FULL";
            if (channel.Entity.Type == "channel-linear")
            {
                manifestVariant = "WEB";
            }
            var tuneSource = await session.apiClient.TuneSourceAsync(new()
            {
                Id = channelId,
                HlsVersion = "V3",
                ManifestVariant = manifestVariant,
                MtcVersion = "V2",
                Type = channel.Entity.Type
            });
            var stream1 = tuneSource.Streams!.First();
            if (channel.Entity.Type == "channel-linear")
            {
                metadataService.UpdateCutsFromStream(channelId, stream1.Metadata?.Live?.Items!.ToList());
                return stream1;
            }
            else
            {
                //var metadata = stream1.Metadata!.
                return stream1;
            }
        }
        catch (HttpRequestException hex)
        {
            logger.LogWarning(hex, $"HTTP error during TuneSource - {hex.Message} - retrying - retries={retries}");
            await Task.Delay(TimeSpan.FromSeconds(5 * (retries + 1)), tokenSource.Token);
            await session.ReLogin();
            sxmSessionService.InitializeActivityTimer();
            return await TuneSourceAsync(channelId, retries + 1);
        }
        catch (ApiException aex)
        {
            if (aex.StatusCode == 200)
            {
                logger.LogCritical(aex, $"Error loading cuts {aex.Message}");
                throw new InvalidOperationException("Error loading cuts");
            }
            else
            {
                logger.LogWarning($"Error loading cuts - error {aex.StatusCode}:{aex.Response ?? aex.Message} - retrying - retries={retries}");
                await Task.Delay(TimeSpan.FromSeconds(5 * (retries + 1)), tokenSource.Token);
                await session.ReLogin();
                sxmSessionService.InitializeActivityTimer();
                return await TuneSourceAsync(channelId, retries + 1);
            }
        }
    }
}
