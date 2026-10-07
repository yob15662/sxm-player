using System;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Owns current-channel state and the channel-change signal. Implemented by
/// <see cref="MetadataService"/>.
/// </summary>
/// <remarks>
/// <see cref="ChannelChanged"/> is a <b>single-subscriber</b> seam: only
/// <see cref="SiriusXMPlayer"/> subscribes to it (it reproduces the player-owned producer-restart
/// signal). <see cref="MetadataService"/> does not fan it out and no second <c>+=</c> is permitted.
/// </remarks>
public interface ICurrentChannelService
{
    Task<ChannelItemData?> GetCurrentChannelAsync();

    /// <summary>
    /// Sets the current channel and, when it actually changed, raises
    /// <see cref="ChannelChanged"/> (awaiting the handler before returning).
    /// </summary>
    Task SetCurrentChannelAsync(string channelId);

    /// <summary>
    /// Raised exactly once per genuine channel change. Single-subscriber (see remarks).
    /// </summary>
    event Func<string, Task>? ChannelChanged;
}
