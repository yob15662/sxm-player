namespace SXMPlayer.Tests;

/// <summary>
/// Unit tests for AacFrameAnalyzer - ADTS frame parsing utilities.
/// </summary>
public class AacFrameAnalyzerTests
{
    /// <summary>
    /// Valid ADTS frame header format:
    /// 0xFF 0xFx = sync marker (12 bits: 1111 1111 1111)
    /// </summary>
    private static byte[] CreateValidAdtsFrame(int frameSize = 200)
    {
        var frame = new byte[Math.Max(frameSize, 7)];

        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = 0x50;
        frame[3] = 0x80; // channel_configuration = 2 (stereo)

        int encodedSize = frameSize;
        frame[3] = (byte)((frame[3] & 0xFC) | ((encodedSize >> 11) & 0x03));
        frame[4] = (byte)((encodedSize >> 3) & 0xFF);
        frame[5] = (byte)((encodedSize & 0x07) << 5);

        return frame;
    }

    private static void EncodeFrameSize(byte[] frame, int frameSize)
    {
        frame[3] = (byte)((frame[3] & 0xFC) | ((frameSize >> 11) & 0x03));
        frame[4] = (byte)((frameSize >> 3) & 0xFF);
        frame[5] = (byte)((frameSize & 0x07) << 5);
    }

    [Fact]
    public void TryDetectFrameSize_WithValidAdtsHeader_ReturnsFrameSize()
    {
        // Arrange
        int expectedSize = 256;
        var frame = CreateValidAdtsFrame(expectedSize);

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        // Assert
        Assert.Equal(expectedSize, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithTooShortBuffer_ReturnsNegativeOne()
    {
        // Arrange
        var shortFrame = new byte[5]; // Less than 6 bytes required
        shortFrame[0] = 0xFF;
        shortFrame[1] = 0xF1;

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(shortFrame);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithInvalidSyncMarker_ReturnsNegativeOne()
    {
        // Arrange
        var frame = new byte[10];
        frame[0] = 0xFE; // Invalid: not 0xFF

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithFrameSizeTooSmall_ReturnsNegativeOne()
    {
        // Arrange
        var frame = new byte[10];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = 0x50;
        int smallSize = 5;
        EncodeFrameSize(frame, smallSize);

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithFrameSizeTooLarge_ReturnsNegativeOne()
    {
        // Arrange
        var frame = new byte[10];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = 0x50;
        int invalidSize = 8193;
        EncodeFrameSize(frame, invalidSize);

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithInvalidLayerBits_ReturnsNegativeOne()
    {
        var frame = CreateValidAdtsFrame(128);
        frame[1] = 0xF3;

        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithReservedSamplingFrequency_ReturnsNegativeOne()
    {
        var frame = CreateValidAdtsFrame(128);
        frame[2] = 0x3C;

        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void TryDetectFrameSize_WithCrcHeaderSmallerThanNineBytes_ReturnsNegativeOne()
    {
        var frame = CreateValidAdtsFrame(8);
        frame[1] = 0xF0;
        EncodeFrameSize(frame, 8);

        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindNextFrameBoundary_WithSingleValidFrame_ReturnsFrameOffset()
    {
        // Arrange
        var data = new byte[300];
        // Put frame at offset 50
        int frameOffset = 50;
        var validFrame = CreateValidAdtsFrame(200);
        Array.Copy(validFrame, 0, data, frameOffset, validFrame.Length);

        // Act
        int result = AacFrameAnalyzer.FindNextFrameBoundary(data.AsSpan());

        // Assert
        Assert.Equal(frameOffset, result);
    }

    [Fact]
    public void FindNextFrameBoundary_WithNoValidFrame_ReturnsDataLength()
    {
        // Arrange
        var data = new byte[100];
        Array.Fill(data, (byte)0x00); // All zeros, no valid frames

        // Act
        int result = AacFrameAnalyzer.FindNextFrameBoundary(data.AsSpan());

        // Assert
        Assert.Equal(data.Length, result);
    }

    [Fact]
    public void IsValidAdtsHeader_WithValidHeader_ReturnsTrue()
    {
        // Arrange
        var frame = CreateValidAdtsFrame(200);

        // Act
        bool result = AacFrameAnalyzer.IsValidAdtsHeader(frame);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidAdtsHeader_WithInvalidHeader_ReturnsFalse()
    {
        // Arrange
        var frame = new byte[10];
        Array.Fill(frame, (byte)0x00);

        // Act
        bool result = AacFrameAnalyzer.IsValidAdtsHeader(frame);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidAdtsHeader_WithTooShortBuffer_ReturnsFalse()
    {
        // Arrange
        var frame = new byte[5];

        // Act
        bool result = AacFrameAnalyzer.IsValidAdtsHeader(frame);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(7)]      // Minimum valid size
    [InlineData(256)]    // Common size
    [InlineData(4096)]   // Large frame
    public void TryDetectFrameSize_WithVariousSizes_ReturnsCorrectSize(int size)
    {
        // Arrange
        var frame = CreateValidAdtsFrame(size);

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        // Assert
        Assert.Equal(size, result);
    }

    [Fact]
    public void FindNextFrameBoundary_WithMultipleFrames_ReturnsFirstOne()
    {
        // Arrange
        var data = new byte[500];
        var frame1 = CreateValidAdtsFrame(100);
        var frame2 = CreateValidAdtsFrame(150);

        Array.Copy(frame1, 0, data, 50, frame1.Length);
        Array.Copy(frame2, 0, data, 150, frame2.Length);

        // Act
        int result = AacFrameAnalyzer.FindNextFrameBoundary(data.AsSpan());

        // Assert
        Assert.Equal(50, result);
    }

    [Fact]
    public void FindNextFrameBoundary_WithFalsePositiveBeforeRealFrame_SkipsFalsePositive()
    {
        var data = new byte[400];

        data[0] = 0xFF;
        data[1] = 0xF1;
        data[2] = 0x50;
        EncodeFrameSize(data, 60);

        var frame1 = CreateValidAdtsFrame(100);
        var frame2 = CreateValidAdtsFrame(100);
        Array.Copy(frame1, 0, data, 80, frame1.Length);
        Array.Copy(frame2, 0, data, 180, frame2.Length);

        int result = AacFrameAnalyzer.FindNextFrameBoundary(data.AsSpan());

        Assert.Equal(80, result);
    }

    [Fact]
    public void FindNextFrameBoundary_WithTruncatedFalsePositiveBeforeRealFrame_SkipsTruncatedCandidate()
    {
        var data = new byte[240];
        const int truncatedFrameSize = 240;

        data[10] = 0xFF;
        data[11] = 0xF1;
        data[12] = 0x50;
        data[13] = (byte)((truncatedFrameSize >> 11) & 0x03);
        data[14] = (byte)((truncatedFrameSize >> 3) & 0xFF);
        data[15] = (byte)((truncatedFrameSize & 0x07) << 5);

        var frame1 = CreateValidAdtsFrame(80);
        var frame2 = CreateValidAdtsFrame(80);
        Array.Copy(frame1, 0, data, 80, frame1.Length);
        Array.Copy(frame2, 0, data, 160, frame2.Length);

        int result = AacFrameAnalyzer.FindNextFrameBoundary(data.AsSpan());

        Assert.Equal(80, result);
    }
}
