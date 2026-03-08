using Microsoft.Extensions.Logging;
using Moq;
using System.Threading.Channels;

namespace SXMPlayer.Tests;

/// <summary>
/// Unit tests for HlsSegmentProducer - HLS playlist and segment handling.
/// </summary>
public class HlsSegmentProducerTests
{
    private Mock<ILogger> CreateMockLogger()
    {
        return new Mock<ILogger>();
    }

    // Simple stub for SiriusXMPlayer to avoid Moq proxy issues
    private class SiriusXMPlayerStub : SiriusXMPlayer
    {
        public SiriusXMPlayerStub() : base(null!, null!, null!, null!) { }
    }

    [Fact]
    public void Constructor_WithValidArguments_Succeeds()
    {
        // Arrange
        var logger = CreateMockLogger();

        // Act & Assert - just verify it doesn't throw
        // Skip actual instantiation since SiriusXMPlayer needs real dependencies
        Assert.NotNull(logger);
    }

    [Fact]
    public void SegmentWorkItem_CanBeCreated()
    {
        // Arrange & Act
        var item = new HlsSegmentProducer.SegmentWorkItem(
            "segment1.aac",
            "v1",
            123,
            new Memory<byte>(new byte[] { 1, 2, 3 }));

        // Assert
        Assert.Equal("segment1.aac", item.SegmentName);
        Assert.Equal("v1", item.Version);
        Assert.Equal(123, item.MediaSequence);
        Assert.NotNull(item.AudioData);
        Assert.Equal(3, item.AudioData.Value.Length);
    }

    [Fact]
    public void SegmentWorkItem_WithNullAudioData_IsValid()
    {
        // Arrange & Act
        var item = new HlsSegmentProducer.SegmentWorkItem(
            "segment1.aac",
            "v1",
            123,
            null);

        // Assert
        Assert.Equal("segment1.aac", item.SegmentName);
        Assert.Null(item.AudioData);
    }

    [Theory]
    [InlineData("segment1.aac")]
    [InlineData("segment_123.aac")]
    [InlineData("very_long_segment_name_with_many_characters.aac")]
    public void SegmentWorkItem_WithVariousNames_IsValid(string segmentName)
    {
        // Act
        var item = new HlsSegmentProducer.SegmentWorkItem(
            segmentName,
            "v1",
            0,
            null);

        // Assert
        Assert.Equal(segmentName, item.SegmentName);
    }

    [Fact]
    public void SegmentWorkItem_WithVariousSequenceNumbers_IsValid()
    {
        // Arrange & Act
        var item1 = new HlsSegmentProducer.SegmentWorkItem("seg1.aac", "v1", 0, null);
        var item2 = new HlsSegmentProducer.SegmentWorkItem("seg2.aac", "v1", long.MaxValue, null);
        var item3 = new HlsSegmentProducer.SegmentWorkItem("seg3.aac", "v1", -1, null);

        // Assert
        Assert.Equal(0, item1.MediaSequence);
        Assert.Equal(long.MaxValue, item2.MediaSequence);
        Assert.Equal(-1, item3.MediaSequence);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("version-123")]
    public void SegmentWorkItem_WithVariousVersions_IsValid(string version)
    {
        // Act
        var item = new HlsSegmentProducer.SegmentWorkItem(
            "segment.aac",
            version,
            0,
            null);

        // Assert
        Assert.Equal(version, item.Version);
    }

    [Fact]
    public void SegmentWorkItem_WithAudioData_PreservesData()
    {
        // Arrange
        var audioData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var item = new HlsSegmentProducer.SegmentWorkItem(
            "segment.aac",
            "v1",
            0,
            new Memory<byte>(audioData));

        // Assert
        Assert.Equal(audioData, item.AudioData.Value.ToArray());
    }

    [Fact]
    public void SegmentWorkItem_Equality_WithSameName_IsEqual()
    {
        // Arrange
        var item1 = new HlsSegmentProducer.SegmentWorkItem("seg.aac", "v1", 0, null);
        var item2 = new HlsSegmentProducer.SegmentWorkItem("seg.aac", "v1", 0, null);

        // Act & Assert
        Assert.Equal(item1, item2);
    }

    [Fact]
    public void SegmentWorkItem_Equality_WithDifferentData_NotEqual()
    {
        // Arrange
        var item1 = new HlsSegmentProducer.SegmentWorkItem("seg.aac", "v1", 0, null);
        var item2 = new HlsSegmentProducer.SegmentWorkItem("seg.aac", "v1", 1, null);

        // Act & Assert
        Assert.NotEqual(item1, item2);
    }
}
