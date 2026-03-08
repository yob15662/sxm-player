using System;
using System.Text;

namespace SXMPlayer;

/// <summary>
/// Builds ICY (Shoutcast) metadata blocks for Icecast/Shoutcast streams.
/// </summary>
public class IcyMetadataBuilder
{
    private string? _lastStreamTitle;
    private DateTime _lastMetadataUpdate = DateTime.MinValue;

    /// <summary>
    /// Gets or sets the metadata injection interval in bytes.
    /// </summary>
    public int MetadataInterval { get; set; } = 8162;

    /// <summary>
    /// Builds an ICY metadata block from now-playing data.
    /// Returns empty metadata (1 byte: 0x00) if title hasn't changed within the debounce window.
    /// </summary>
    /// <param name="nowPlaying">Current now-playing information, or null if unavailable</param>
    /// <returns>ICY metadata block (length byte + padded metadata string)</returns>
    public byte[] BuildMetadataBlock(NowPlayingData? nowPlaying)
    {
        if (nowPlaying is null)
        {
            _lastStreamTitle = null;
            return new byte[] { 0 };
        }

        var streamTitle = $"{nowPlaying.artist} - {nowPlaying.song}";

        // Debounce: only send metadata if title changed or after 10 seconds
        if (streamTitle == _lastStreamTitle && DateTime.UtcNow <= _lastMetadataUpdate.AddSeconds(10))
        {
            return new byte[] { 0 };
        }

        // Remove single quotes to avoid breaking the ICY format
        streamTitle = streamTitle.Replace("'", "");
        var metadataString = $"StreamTitle='{streamTitle}';";

        _lastStreamTitle = streamTitle;
        _lastMetadataUpdate = DateTime.UtcNow;

        return FormatMetadataBlock(metadataString);
    }

    /// <summary>
    /// Clears the cached metadata state to force refresh on next call.
    /// </summary>
    public void ClearState()
    {
        _lastStreamTitle = null;
    }

    /// <summary>
    /// Formats a metadata string into the ICY block format.
    /// ICY format: 1 length byte (in 16-byte blocks) + zero-padded metadata
    /// </summary>
    private static byte[] FormatMetadataBlock(string metadataString)
    {
        var metadataBytes = Encoding.UTF8.GetBytes(metadataString);
        var metadataLength = (metadataBytes.Length + 15) / 16; // Round up to 16-byte blocks
        var paddedLength = metadataLength * 16;

        var result = new byte[paddedLength + 1];
        result[0] = (byte)metadataLength;

        Array.Copy(metadataBytes, 0, result, 1, metadataBytes.Length);

        return result;
    }
}
