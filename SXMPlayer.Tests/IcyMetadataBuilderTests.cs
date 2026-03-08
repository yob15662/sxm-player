using System.Text;

namespace SXMPlayer.Tests;

/// <summary>
/// Unit tests for IcyMetadataBuilder - ICY metadata generation.
/// </summary>
public class IcyMetadataBuilderTests
{
    [Fact]
    public void BuildMetadataBlock_WithValidNowPlaying_ReturnsIcyBlock()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("TestChannel", "Test Artist", "Test Song", "track-123");

        // Act
        byte[] result = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
        // First byte is length in 16-byte blocks
        int metaLength = result[0];
        Assert.True(metaLength > 0);
        // Total length should be 1 (length byte) + metaLength * 16
        Assert.Equal(1 + metaLength * 16, result.Length);
    }

    [Fact]
    public void BuildMetadataBlock_WithNullNowPlaying_ReturnsEmptyBlock()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();

        // Act
        byte[] result = builder.BuildMetadataBlock(null);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result); // Should be single 0x00 byte
        Assert.Equal(0, result[0]);
    }

    [Fact]
    public void BuildMetadataBlock_MetadataContainsStreamTitle()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("Channel", "Artist", "Song", "id");

        // Act
        byte[] result = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        int metaLength = result[0];
        int metadataSize = metaLength * 16;
        var metadataString = Encoding.UTF8.GetString(result, 1, metadataSize).TrimEnd('\0');
        Assert.Contains("StreamTitle=", metadataString);
        Assert.Contains("Artist", metadataString);
        Assert.Contains("Song", metadataString);
    }

    [Fact]
    public void BuildMetadataBlock_DebouncesPreviousTitle()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("Channel", "Artist", "Song", "id");

        // Act - first call should return metadata
        byte[] result1 = builder.BuildMetadataBlock(nowPlaying);
        // Second call with same data should return empty metadata (debounce)
        byte[] result2 = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        Assert.True(result1[0] > 0); // First has metadata
        Assert.Single(result2); // Second is empty
        Assert.Equal(0, result2[0]);
    }

    [Fact]
    public void BuildMetadataBlock_RemovesSingleQuotes()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("Channel", "Artist'sName", "Song's Title", "id");

        // Act
        byte[] result = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        int metaLength = result[0];
        int metadataSize = metaLength * 16;
        var metadataString = Encoding.UTF8.GetString(result, 1, metadataSize).TrimEnd('\0');
        // Should not contain unescaped single quotes
        Assert.DoesNotContain("'s", metadataString);
    }

    [Fact]
    public void MetadataInterval_CanBeSet()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();

        // Act
        builder.MetadataInterval = 16384;

        // Assert
        Assert.Equal(16384, builder.MetadataInterval);
    }

    [Fact]
    public void ClearState_ResetsMetadataCache()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("Channel", "Artist", "Song", "id");
        
        // First call generates metadata
        byte[] result1 = builder.BuildMetadataBlock(nowPlaying);
        Assert.True(result1[0] > 0); // Has metadata

        // Clear state
        builder.ClearState();

        // Act - second call should regenerate metadata
        byte[] result2 = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        Assert.True(result2[0] > 0); // Should have metadata again after clear
    }

    [Fact]
    public void BuildMetadataBlock_PadsToSixteenByteBlocks()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("Ch", "A", "S", "id");

        // Act
        byte[] result = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        int metaLength = result[0];
        // Length should be exact multiple of 16 plus 1 for length byte
        Assert.Equal(0, (result.Length - 1) % 16);
        Assert.True(result.Length >= 18); // At least 1 block (16 bytes) + length byte
    }

    [Fact]
    public void BuildMetadataBlock_DifferentTitles_GeneratesNewMetadata()
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying1 = new NowPlayingData("Channel", "Artist1", "Song1", "id1");
        var nowPlaying2 = new NowPlayingData("Channel", "Artist2", "Song2", "id2");

        // Act
        byte[] result1 = builder.BuildMetadataBlock(nowPlaying1);
        byte[] result2 = builder.BuildMetadataBlock(nowPlaying2);

        // Assert
        Assert.True(result1[0] > 0); // First has metadata
        Assert.True(result2[0] > 0); // Second should also have metadata (different title)

        // Metadata content should be different
        var meta1 = Encoding.UTF8.GetString(result1, 1, Math.Min(50, result1.Length - 1));
        var meta2 = Encoding.UTF8.GetString(result2, 1, Math.Min(50, result2.Length - 1));
        Assert.NotEqual(meta1, meta2);
    }

    [Theory]
    [InlineData("Simple Song")]
    [InlineData("Song with numbers 123")]
    [InlineData("Song-with-dashes")]
    [InlineData("SongWithCapitals")]
    [InlineData("")]
    public void BuildMetadataBlock_WithVariousTitles_HandlesCorrectly(string songTitle)
    {
        // Arrange
        var builder = new IcyMetadataBuilder();
        var nowPlaying = new NowPlayingData("Channel", "Artist", songTitle, "id");

        // Act
        byte[] result = builder.BuildMetadataBlock(nowPlaying);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Length >= 1);
        int metaLength = result[0];
        Assert.Equal(1 + metaLength * 16, result.Length);
    }
}
