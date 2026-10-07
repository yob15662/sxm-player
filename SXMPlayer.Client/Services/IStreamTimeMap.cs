using System;
using System.Collections.Generic;

namespace SXMPlayer;

/// <summary>
/// Read-only view of the segment-name to program-date-time map produced while rewriting the
/// media playlist. Implemented by <see cref="PlaylistService"/>; consumed by
/// <see cref="MetadataService"/> via a post-construction setter to break the construction cycle.
/// </summary>
public interface IStreamTimeMap
{
    IReadOnlyDictionary<string, DateTimeOffset?> StreamTimeMap { get; }
}
