using System;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Now-playing title lookup and fallback snapshot. Implemented by <see cref="MetadataService"/>.
/// </summary>
public interface INowPlayingProvider
{
    /// <summary>
    /// Resolves the now-playing track for a channel at a given timestamp. The three-parameter
    /// signature (including the defaulted <paramref name="tryRefresh"/>) matches the concrete
    /// <see cref="MetadataService.GetNowPlaying(string, DateTimeOffset?, bool)"/> exactly, so the
    /// concrete method implicitly implements this member.
    /// </summary>
    Task<(string artist, string title, string? id)?> GetNowPlaying(
        string channelId, DateTimeOffset? ts, bool tryRefresh = true);

    /// <summary>
    /// Returns the current now-playing snapshot (fallback for title building), or <c>null</c>.
    /// </summary>
    NowPlayingData? GetNowPlaying();
}
