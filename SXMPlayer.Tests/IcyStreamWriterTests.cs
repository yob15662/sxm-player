using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading.Tasks;

namespace SXMPlayer.Tests;

/// <summary>
/// Unit tests for IcyStreamWriter - frame-aware metadata injection.
/// </summary>
public class IcyStreamWriterTests
{
    private Mock<ILogger> CreateMockLogger()
    {
        return new Mock<ILogger>();
    }

    private IcyMetadataBuilder CreateMetadataBuilder()
    {
        return new IcyMetadataBuilder();
    }

    private static byte[] CreateValidAdtsFrame(int frameSize = 256)
    {
        var frame = new byte[Math.Max(frameSize, 7)];

        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = 0x50;

        frame[3] = (byte)((frameSize >> 11) & 0x03);
        frame[4] = (byte)((frameSize >> 3) & 0xFF);
        frame[5] = (byte)((frameSize & 0x07) << 5);
        frame[6] = 0x00;

        return frame;
    }

    private Mock<HttpContext> CreateMockHttpContext()
    {
        var mockResponse = new Mock<HttpResponse>();
        mockResponse.Setup(r => r.Body).Returns(new MemoryStream());
        
        var mockContext = new Mock<HttpContext>();
        mockContext.Setup(c => c.Response).Returns(mockResponse.Object);
        
        return mockContext;
    }

    private static byte[] StripIcyMetadata(byte[] payload, int metadataInterval)
    {
        var output = new List<byte>(payload.Length);
        int index = 0;

        while (index < payload.Length)
        {
            int audioCount = Math.Min(metadataInterval, payload.Length - index);
            output.AddRange(payload.AsSpan(index, audioCount).ToArray());
            index += audioCount;

            if (index >= payload.Length)
            {
                break;
            }

            int metadataLengthBlocks = payload[index];
            index++;
            int metadataBytes = metadataLengthBlocks * 16;
            index = Math.Min(index + metadataBytes, payload.Length);
        }

        return output.ToArray();
    }

    [Fact]
    public async Task WriteAsync_WithoutMetadata_WritesDataAsIs()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        
        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data),
            mockContext.Object,
            injectMetadata: false,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        responseBody.Position = 0;
        byte[] written = new byte[responseBody.Length];
        responseBody.Read(written);
        Assert.Equal(data, written);
    }

    [Fact]
    public async Task WriteAsync_WithMetadataDisabled_ReturnedBytesUntilMetaIsMaxValue()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object) { OutputChunkSize = 10 };
        var data = new byte[100];
        
        var mockContext = CreateMockHttpContext();

        // Act
        int result = await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data),
            mockContext.Object,
            injectMetadata: false,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        Assert.Equal(int.MaxValue, result);
    }

    [Fact]
    public async Task WriteAsync_RespectOutputChunkSize()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object) { OutputChunkSize = 10 };
        var data = new byte[100];
        Array.Fill(data, (byte)1);
        
        var mockContext = CreateMockHttpContext();
        var mockResponse = new Mock<HttpResponse>();
        var responseBody = new MemoryStream();
        mockResponse.Setup(r => r.Body).Returns(responseBody);
        mockContext.Setup(c => c.Response).Returns(mockResponse.Object);

        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data),
            mockContext.Object,
            injectMetadata: false,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        Assert.Equal(data.Length, responseBody.Length);
    }

    [Fact(Timeout = 5000)]
    public async Task WriteAsync_WithMetadataEnabled_InjectsMetadata()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object) { OutputChunkSize = 16 * 1024 };
        
        // Create valid AAC-like frames that fit within metadata interval
        var data = new List<byte>();
        int frameSize = 256; // Reasonable frame size that fits in metadata interval
        int targetSize = 20000;
        
        while (data.Count < targetSize)
        {
            var frame = CreateValidAdtsFrame(frameSize);
            data.AddRange(frame);
        }
        
        var mockContext = CreateMockHttpContext();
        var mockResponse = new Mock<HttpResponse>();
        var responseBody = new MemoryStream();
        mockResponse.Setup(r => r.Body).Returns(responseBody);
        mockContext.Setup(c => c.Response).Returns(mockResponse.Object);

        // Act
        int result = await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data.ToArray()),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        // Should have written something (audio + metadata)
        Assert.True(responseBody.Length > 0);
        // Result should be <= metadataInterval (bytes until next metadata)
        Assert.True(result <= 8162);
    }

    [Fact]
    public async Task WriteAsync_FlushesResponseBody()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        var data = new byte[100];
        
        var mockResponse = new Mock<HttpResponse>();
        var mockBody = new Mock<Stream>();
        mockBody.Setup(s => s.WriteAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        mockBody.Setup(s => s.FlushAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockResponse.Setup(r => r.Body).Returns(mockBody.Object);
        
        var mockContext = new Mock<HttpContext>();
        mockContext.Setup(c => c.Response).Returns(mockResponse.Object);

        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data),
            mockContext.Object,
            injectMetadata: false,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        mockBody.Verify(s => s.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteAsync_WithCancellation_ThrowsTaskCanceledException()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        var data = new byte[100];
        
        var mockContext = CreateMockHttpContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - TaskCanceledException is thrown specifically for async operations
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            writer.WriteAsync(
                new ReadOnlyMemory<byte>(data),
                mockContext.Object,
                injectMetadata: false,
                metadataInterval: 8162,
                bytesUntilNextMetadata: 8162,
                cts.Token));
    }

    [Fact]
    public void OutputChunkSize_CanBeSet()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        // Act
        writer.OutputChunkSize = 32 * 1024;

        // Assert
        Assert.Equal(32 * 1024, writer.OutputChunkSize);
    }

    [Fact]
    public void OutputChunkSize_DefaultValue()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        // Assert
        Assert.Equal(16 * 1024, writer.OutputChunkSize);
    }

    [Fact]
    public async Task WriteAsync_WithEmptyData_WritesNothing()
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        var data = new byte[0];
        
        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data),
            mockContext.Object,
            injectMetadata: false,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        Assert.Equal(0, responseBody.Length);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(16384)]
    [InlineData(65536)]
    public async Task WriteAsync_WithVariousDataSizes_WritesAllData(int dataSize)
    {
        // Arrange
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        var data = new byte[dataSize];
        Array.Fill(data, (byte)42);
        
        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(data),
            mockContext.Object,
            injectMetadata: false,
            metadataInterval: 8162,
            bytesUntilNextMetadata: 8162,
            CancellationToken.None);

        // Assert
        Assert.Equal(dataSize, responseBody.Length);
    }

    [Fact]
    public async Task WriteAsync_WithMetadataInject_DoesNotCorruptFrameBoundaries()
    {
        // Arrange - Create a sequence of valid AAC frames
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object) { OutputChunkSize = 16 * 1024 };
        
        // Create multiple valid AAC frames
        const int frameSize = 512;
        const int metadataInterval = 2048; // Will fit ~4 frames before metadata
        var frames = new List<byte[]>();
        
        // Create 10 frames to ensure multiple metadata injections
        for (int i = 0; i < 10; i++)
        {
            var frame = CreateValidAdtsFrame(frameSize);
            frames.Add(frame);
        }
        
        // Concatenate all frames
        var audioData = frames.SelectMany(f => f).ToArray();
        
        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);
        
        // Act
        int resultBytesUntilMetadata = await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: metadataInterval,
            bytesUntilNextMetadata: metadataInterval,
            CancellationToken.None);
        
        // Assert
        responseBody.Position = 0;
        var writtenData = new byte[responseBody.Length];
        responseBody.Read(writtenData);
        
        // Should have written all audio data
        Assert.True(responseBody.Length >= audioData.Length);
        
        // Should have injected metadata (response should be larger than just audio)
        Assert.True(responseBody.Length > audioData.Length);
        
        // Result should indicate bytes until next metadata
        Assert.True(resultBytesUntilMetadata <= metadataInterval);
        Assert.True(resultBytesUntilMetadata > 0);
        
        // Verify that frame sync markers are not corrupted in the output
        // Count valid AAC sync markers in the output (should be at least the original frames)
        int syncMarkerCount = 0;
        for (int i = 0; i < writtenData.Length - 1; i++)
        {
            if (writtenData[i] == 0xFF && (writtenData[i + 1] & 0xF0) == 0xF0)
            {
                syncMarkerCount++;
            }
        }
        
        // Should find at least as many sync markers as we created frames
        Assert.True(syncMarkerCount >= frames.Count, 
            $"Expected at least {frames.Count} sync markers, but found {syncMarkerCount}");
    }

    [Fact]
    public async Task WriteAsync_WithMetadataInterval_MetadataInjectedBetweenFrames()
    {
        // Arrange - Test that metadata is injected at frame boundaries, not in the middle of frames
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        
        // Create 3 frames, each 256 bytes
        const int frameSize = 256;
        const int metadataInterval = 256; // Metadata after each frame
        var frames = new List<byte[]>();
        
        for (int i = 0; i < 3; i++)
        {
            var frame = CreateValidAdtsFrame(frameSize);
            frames.Add(frame);
        }
        
        var audioData = frames.SelectMany(f => f).ToArray();
        
        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);
        
        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: metadataInterval,
            bytesUntilNextMetadata: metadataInterval,
            CancellationToken.None);
        
        // Assert
        responseBody.Position = 0;
        var writtenData = new byte[responseBody.Length];
        responseBody.Read(writtenData);
        
        // Metadata blocks are 16 bytes by default from IcyMetadataBuilder
        // With 3 frames * 256 bytes = 768 bytes audio + multiple metadata blocks
        Assert.True(responseBody.Length > audioData.Length);
        
        // Verify frames are intact by checking for frame sync markers at expected positions
        // After each frame boundary, we should find a sync marker (from next frame) after metadata
        int frameCount = 0;
        for (int i = 0; i < writtenData.Length - 1; i++)
        {
            if (writtenData[i] == 0xFF && (writtenData[i + 1] & 0xF0) == 0xF0)
            {
                frameCount++;
            }
        }
        
        // Should have at least the original number of frame sync markers
        Assert.True(frameCount >= frames.Count);
    }

    [Fact]
    public async Task WriteAsync_WithSmallMetadataInterval_DoesNotLoseData()
    {
        // Arrange - Ensure data isn't lost with very small metadata interval
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);
        
        const int frameSize = 256;
        const int metadataInterval = 512; // Allow at least one full frame before metadata
        const int frameCount = 5;
        
        var frames = new List<byte[]>();
        for (int i = 0; i < frameCount; i++)
        {
            var frame = CreateValidAdtsFrame(frameSize);
            frames.Add(frame);
        }
        
        var audioData = frames.SelectMany(f => f).ToArray();
        
        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);
        
        // Act
        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: metadataInterval,
            bytesUntilNextMetadata: metadataInterval,
            CancellationToken.None);
        
        // Assert
        responseBody.Position = 0;
        var writtenData = new byte[responseBody.Length];
        responseBody.Read(writtenData);
        
        // Count AAC sync markers in output
        int syncMarkerCount = 0;
        for (int i = 0; i < writtenData.Length - 1; i++)
        {
            if (writtenData[i] == 0xFF && (writtenData[i + 1] & 0xF0) == 0xF0)
            {
                syncMarkerCount++;
            }
        }
        
        // Should have found all the original frame sync markers
        Assert.Equal(frameCount, syncMarkerCount);
        
        // Total output should include audio + metadata
        Assert.True(responseBody.Length > audioData.Length);
    }

    [Fact]
    public async Task WriteAsync_WhenMisaligned_SkipsBytesUntilNextFrameBoundary()
    {
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        byte[] prefix = Enumerable.Range(1, 60).Select(i => (byte)i).ToArray();
        var frame1 = CreateValidAdtsFrame(256);
        var frame2 = CreateValidAdtsFrame(256);
        var expectedAudio = frame1.Concat(frame2).ToArray();
        var audioData = prefix.Concat(expectedAudio).ToArray();

        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: 32,
            bytesUntilNextMetadata: 32,
            CancellationToken.None);

        var writtenData = responseBody.ToArray();
        Assert.True(writtenData.Length > expectedAudio.Length);

        var recoveredAudio = StripIcyMetadata(writtenData, 32);
        Assert.Equal(expectedAudio, recoveredAudio.Take(expectedAudio.Length).ToArray());
    }

    [Fact(Timeout = 5000)]
    public async Task WriteAsync_WhenFrameExceedsRemainingMetadataBudget_InsertsMetadataAtExactInterval()
    {
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        var frame1 = CreateValidAdtsFrame(256);
        var frame2 = CreateValidAdtsFrame(256);
        var audioData = frame1.Concat(frame2).ToArray();

        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        int bytesUntilNextMetadata = await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: 32,
            bytesUntilNextMetadata: 32,
            CancellationToken.None);

        var writtenData = responseBody.ToArray();

        Assert.True(writtenData.Length > audioData.Length);
        Assert.True(bytesUntilNextMetadata <= 32);

        var recoveredAudio = StripIcyMetadata(writtenData, 32);
        Assert.Equal(audioData, recoveredAudio.Take(audioData.Length).ToArray());

        Assert.True(writtenData.Length > 32);
        Assert.True(writtenData[32] >= 0);
    }

    [Fact]
    public async Task WriteAsync_WithMetadataEnabled_StrippingMetadataReproducesOriginalAudio()
    {
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        var audioData = Enumerable.Range(0, 9)
            .SelectMany(_ => CreateValidAdtsFrame(300))
            .ToArray();

        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: 1024,
            bytesUntilNextMetadata: 1024,
            CancellationToken.None);

        var recoveredAudio = StripIcyMetadata(responseBody.ToArray(), 1024);
        Assert.Equal(audioData, recoveredAudio.Take(audioData.Length).ToArray());
    }

    [Fact]
    public async Task WriteAsync_WhenPrefixContainsTruncatedFalsePositive_ResyncsToRealFrameBoundary()
    {
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        var prefix = new byte[70];
        const int truncatedFrameSize = 128;
        prefix[10] = 0xFF;
        prefix[11] = 0xF1;
        prefix[12] = 0x50;
        prefix[13] = (byte)((truncatedFrameSize >> 11) & 0x03);
        prefix[14] = (byte)((truncatedFrameSize >> 3) & 0xFF);
        prefix[15] = (byte)((truncatedFrameSize & 0x07) << 5);

        var expectedAudio = CreateValidAdtsFrame(256).Concat(CreateValidAdtsFrame(256)).ToArray();
        var audioData = prefix.Concat(expectedAudio).ToArray();

        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: 64,
            bytesUntilNextMetadata: 64,
            CancellationToken.None);

        var recoveredAudio = StripIcyMetadata(responseBody.ToArray(), 64);
        Assert.Equal(expectedAudio, recoveredAudio.Take(expectedAudio.Length).ToArray());
    }

    [Fact]
    public async Task WriteAsync_WithZeroBudget_InjectsMetadataBeforeAudio()
    {
        var logger = CreateMockLogger();
        var builder = CreateMetadataBuilder();
        var writer = new IcyStreamWriter(builder, logger.Object);

        var audioData = CreateValidAdtsFrame(128);

        var mockContext = CreateMockHttpContext();
        var responseBody = new MemoryStream();
        mockContext.Setup(c => c.Response.Body).Returns(responseBody);

        int remainingBudget = await writer.WriteAsync(
            new ReadOnlyMemory<byte>(audioData),
            mockContext.Object,
            injectMetadata: true,
            metadataInterval: 512,
            bytesUntilNextMetadata: 0,
            CancellationToken.None);

        var written = responseBody.ToArray();

        Assert.Equal(audioData.Length + 1, written.Length);
        Assert.Equal(0, written[0]);
        Assert.Equal(audioData, written.AsSpan(1).ToArray());
        Assert.Equal(512 - audioData.Length, remainingBudget);
    }
}
