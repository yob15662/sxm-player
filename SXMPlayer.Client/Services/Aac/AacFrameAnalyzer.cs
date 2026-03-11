using System;

namespace SXMPlayer;

/// <summary>
/// Provides utilities for analyzing AAC ADTS (Audio Data Transport Stream) frames.
/// </summary>
public static class AacFrameAnalyzer
{
    private const int MinAdtsHeaderSize = 7;
    private const int MinAdtsHeaderSizeWithCrc = 9;

    /// <summary>
    /// Detects AAC ADTS sync marker and calculates frame size.
    /// ADTS frames start with 0xFFF (sync code in first 12 bits).
    /// </summary>
    /// <param name="frame">Span starting at a potential ADTS sync marker</param>
    /// <returns>Frame size in bytes if a valid ADTS header is found; -1 otherwise</returns>
    public static int TryDetectFrameSize(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < MinAdtsHeaderSize)
            return -1;

        if (!HasSyncWord(frame))
            return -1;

        // Layer must always be 00 for ADTS.
        if ((frame[1] & 0x06) != 0)
            return -1;

        bool protectionAbsent = (frame[1] & 0x01) != 0;
        int headerSize = protectionAbsent ? MinAdtsHeaderSize : MinAdtsHeaderSizeWithCrc;
        if (frame.Length < headerSize)
            return -1;

        int samplingFrequencyIndex = (frame[2] & 0x3C) >> 2;
        if (samplingFrequencyIndex == 0x0F)
            return -1;

        int length = ((frame[3] & 0x03) << 11)
                   | (frame[4] << 3)
                   | ((frame[5] & 0xE0) >> 5);

        if (length < headerSize || length > 8192)
            return -1;

        return length;
    }

    /// <summary>
    /// Finds the next AAC ADTS frame boundary starting from the beginning of the span.
    /// </summary>
    /// <param name="data">Span to search for frame boundaries</param>
    /// <param name="maxSearch">Maximum bytes to search (default 192 KB)</param>
    /// <returns>Offset of the next frame boundary, or data.Length if not found</returns>
    public static int FindNextFrameBoundary(ReadOnlySpan<byte> data, int maxSearch = 192 * 1024)
    {
        if (data.Length < 2)
            return data.Length;

        int searchLimit = Math.Min(data.Length, maxSearch);
        int fallbackOffset = data.Length;

        for (int i = 0; i < searchLimit - 1; i++)
        {
            if (!HasSyncWord(data.Slice(i)))
                continue;

            int frameSize = TryDetectFrameSize(data.Slice(i));
            if (frameSize <= 0)
                continue;

            int nextFramePos = i + frameSize;
            if (nextFramePos >= data.Length)
                return i;

            if (nextFramePos + MinAdtsHeaderSize > data.Length)
            {
                fallbackOffset = Math.Min(fallbackOffset, i);
                continue;
            }

            if (TryDetectFrameSize(data.Slice(nextFramePos)) > 0)
                return i;

            fallbackOffset = Math.Min(fallbackOffset, i);
        }

        return fallbackOffset;
    }

    /// <summary>
    /// Validates that the span starts with a valid ADTS sync word.
    /// </summary>
    public static bool IsValidAdtsHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinAdtsHeaderSize)
            return false;

        return TryDetectFrameSize(data) > 0;
    }

    private static bool HasSyncWord(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return false;

        if (data[0] != 0xFF)
            return false;

        return (data[1] & 0xF0) == 0xF0;
    }
}
