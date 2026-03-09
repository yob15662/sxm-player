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
        var frame = new byte[Math.Max(frameSize, 6)]; // Ensure minimum size
        
        // ADTS sync marker: 0xFF 0xFx (12 bits of 1s)
        frame[0] = 0xFF;
        frame[1] = 0xF0; // Bits 4-7 are the lower 4 bits of sync marker
        
        // Byte 2: MPEG version (2 bits) + Layer (2 bits) + protection_absent (1 bit) + profile (2 bits) + sampling_frequency_index (4 bits)
        // Set reasonable defaults: MPEG-4 (0), Layer (0), protection_absent (1), profile (1), sampling_frequency_index (4 = 44.1kHz)
        frame[2] = 0x50; // 0101 0000 = no CRC, profile 1, 44.1kHz
        
        // Encode frame size in bytes 3-5
        // Frame size = (byte3[1:0] << 11) | (byte4 << 3) | (byte5[7:5])
        int encodedSize = frameSize;
        frame[3] = (byte)((encodedSize >> 11) & 0x03);
        frame[4] = (byte)((encodedSize >> 3) & 0xFF);
        frame[5] = (byte)((encodedSize & 0x07) << 5);
        
        return frame;
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
        shortFrame[1] = 0xF0;

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
        frame[1] = 0xF0;
        // Encode a size < 7 (too small)
        int smallSize = 5;
        frame[3] = (byte)((smallSize >> 11) & 0x03);
        frame[4] = (byte)((smallSize >> 3) & 0xFF);
        frame[5] = (byte)((smallSize & 0x07) << 5);

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
        frame[1] = 0xF0;
        // Encode a size > 8192
        int invalidSize = 8193;
        frame[3] = (byte)((invalidSize >> 11) & 0x03);
        frame[4] = (byte)((invalidSize >> 3) & 0xFF);
        frame[5] = (byte)((invalidSize & 0x07) << 5);

        // Act
        int result = AacFrameAnalyzer.TryDetectFrameSize(frame);

        // Assert
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
        Array.Copy(frame2, 0, data, 200, frame2.Length);

        // Act
        int result = AacFrameAnalyzer.FindNextFrameBoundary(data.AsSpan());

        // Assert
        Assert.Equal(50, result); // Should find first frame
    }
}
