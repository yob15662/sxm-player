using System;

namespace SXMPlayer;

/// <summary>
/// Represents a decrypted HLS audio segment queued for Icecast streaming.
/// </summary>
public record SegmentWorkItem(string SegmentName, string Version, long MediaSequence, Memory<byte>? AudioData);
