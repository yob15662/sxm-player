using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Resolves tuning information (stream URLs + metadata) for a channel. Wraps the SiriusXM
/// tune-source call with its retry/relogin loop.
/// </summary>
public interface IStreamTuner
{
    /// <summary>
    /// Resolves the <see cref="Streams"/> tuning info for <paramref name="channelId"/>.
    /// The <paramref name="retries"/> parameter is an internal recursion knob only; callers
    /// pass only <paramref name="channelId"/> and rely on the <c>= 0</c> default.
    /// </summary>
    Task<Streams> TuneSourceAsync(string channelId, int retries = 0);
}
