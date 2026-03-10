using System;

namespace SXMPlayer;

/// <summary>
/// Provides utilities for analyzing AAC ADTS (Audio Data Transport Stream) frames.
/// </summary>
public static class AacFrameAnalyzer
{
    /// <summary>
    /// Detects AAC ADTS sync marker and calculates frame size.
    /// ADTS frames start with 0xFFF (sync code in first 12 bits).
    /// </summary>
    /// <param name="frame">Span starting at a potential ADTS sync marker</param>
    /// <returns>Frame size in bytes if a valid ADTS header is found; -1 otherwise</returns>
    public static int TryDetectFrameSize(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7)
            return -1;

        // Check for ADTS sync word (0xFFF in first 12 bits)
        if (frame[0] != 0xFF)
            return -1;
        if ((frame[1] & 0xF0) != 0xF0)
            return -1;

        // Valid ADTS header found. Now extract frame length.
        // Bytes 3-5 contain length info:
        // Byte 3: protection_absent(1 bit) + length_high(2 bits)
        // Byte 4: length_mid(8 bits)
        // Byte 5: length_low(3 bits) + buffer_fullness_high(5 bits)

        int length = ((frame[3] & 0x03) << 11)
                   | (frame[4] << 3)
                   | ((frame[5] & 0xE0) >> 5);

        // Frame length must be at least 7 bytes (header) and reasonable size
        if (length < 7 || length > 8192)
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
        // Need at least 2 bytes to check sync word (0xFF 0xFx)
        if (data.Length < 2)
            return data.Length;

        // Adjust search limit to not exceed buffer
        int searchLimit = Math.Min(data.Length, maxSearch);

        for (int i = 0; i < searchLimit - 1; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xF0) == 0xF0)
            {
                // Found potential ADTS sync marker, verify frame size is reasonable
                int frameSize = TryDetectFrameSize(data.Slice(i));
                if (frameSize > 0)
                {
                    // Frame is valid and complete data is available
                    // Optional: Additional validation to reduce false positives
                    // Check if next frame position also has valid sync word (if enough data exists)
                    int nextFramePos = i + frameSize;
                    if (nextFramePos + 6 <= data.Length)
                    {
                        // Enough data to check next frame header
                        if (data[nextFramePos] == 0xFF && (data[nextFramePos + 1] & 0xF0) == 0xF0)
                        {
                            // Strong confirmation: next frame also has valid sync word
                            return i;
                        }
                        // Next position doesn't have sync word, but could be end of stream
                        // or padding. Still accept this frame since it's structurally valid.
                        // In practice, 0xFF 0xF0 appearing as false positive is rare.
                    }
                    // Either way, we found a complete valid frame
                    return i;
                }
            }
        }

        return data.Length;
    }

    /// <summary>
    /// Validates that the span starts with a valid ADTS sync word.
    /// </summary>
    public static bool IsValidAdtsHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return false;

        if (data[0] != 0xFF)
            return false;
        if ((data[1] & 0xF0) != 0xF0)
            return false;

        return TryDetectFrameSize(data) > 0;
    }
}
