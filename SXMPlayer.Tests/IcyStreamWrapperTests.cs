namespace SXMPlayer.Tests;

/// <summary>
/// Unit tests for IcyStreamWrapper - Stream-based metadata injection.
/// </summary>
public class IcyStreamWrapperTests
{
    [Fact]
    public void Constructor_WithValidStream_Succeeds()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });

        // Act
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Assert
        Assert.NotNull(wrapper);
        Assert.True(wrapper.CanRead);
    }

    [Fact]
    public void Constructor_WithNullStream_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new IcyStreamWrapper(null!, 8162, () => new byte[] { 0 }));
    }

    [Fact]
    public void Constructor_WithNonReadableStream_ThrowsArgumentException()
    {
        // Arrange
        var mockStream = new NonReadableStream();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new IcyStreamWrapper(mockStream, 8162, () => new byte[] { 0 }));
    }

    [Fact]
    public void Constructor_WithInvalidMetaInt_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var sourceStream = new MemoryStream();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IcyStreamWrapper(sourceStream, 0, () => new byte[] { 0 }));
    }

    [Fact]
    public void Constructor_WithNullMetadataFactory_ThrowsArgumentNullException()
    {
        // Arrange
        var sourceStream = new MemoryStream();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new IcyStreamWrapper(sourceStream, 8162, null!));
    }

    [Fact]
    public void CanRead_ReturnsTrue()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Assert
        Assert.True(wrapper.CanRead);
    }

    [Fact]
    public void CanSeek_ReturnsFalse()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Assert
        Assert.False(wrapper.CanSeek);
    }

    [Fact]
    public void CanWrite_ReturnsFalse()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Assert
        Assert.False(wrapper.CanWrite);
    }

    [Fact]
    public void Length_ThrowsNotSupportedException()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => _ = wrapper.Length);
    }

    [Fact]
    public void Position_ThrowsNotSupportedException()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => _ = wrapper.Position);
    }

    [Fact]
    public async Task ReadAsync_WithSimpleData_ReadsFromSource()
    {
        // Arrange
        var sourceData = new byte[] { 1, 2, 3, 4, 5 };
        var sourceStream = new MemoryStream(sourceData);
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });
        var buffer = new byte[5];

        // Act
        int read = await wrapper.ReadAsync(buffer, 0, 5, CancellationToken.None);

        // Assert
        Assert.Equal(5, read);
        Assert.Equal(sourceData, buffer);
    }

    [Fact]
    public async Task ReadAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });
        wrapper.Dispose();
        var buffer = new byte[5];

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            wrapper.ReadAsync(buffer, 0, 5, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_WithMetadataInjection_InjectsMetadata()
    {
        // Arrange
        var sourceData = new byte[10000];
        Array.Fill(sourceData, (byte)0xFF);
        var sourceStream = new MemoryStream(sourceData);
        
        var metadataBlock = new byte[] { 1, 0x53, 0x54, 0x00 }; // "ST" metadata
        var wrapper = new IcyStreamWrapper(sourceStream, 100, () => metadataBlock);
        
        var buffer = new byte[200];

        // Act
        int read = await wrapper.ReadAsync(buffer, 0, 200, CancellationToken.None);

        // Assert
        Assert.True(read > 0);
        // Should have mixed audio and metadata
        bool hasAudio = false;
        for (int i = 0; i < read; i++)
        {
            if (buffer[i] == 0xFF)
            {
                hasAudio = true;
                break;
            }
        }
        Assert.True(hasAudio);
    }

    [Fact]
    public void Read_CallsReadAsync()
    {
        // Arrange
        var sourceData = new byte[] { 1, 2, 3 };
        var sourceStream = new MemoryStream(sourceData);
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });
        var buffer = new byte[3];

        // Act
        int read = wrapper.Read(buffer, 0, 3);

        // Assert
        Assert.Equal(3, read);
        Assert.Equal(sourceData, buffer);
    }

    [Fact]
    public async Task Dispose_ClosesSourceStream()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Act
        wrapper.Dispose();

        // Assert - source stream should be closed
        Assert.Throws<ObjectDisposedException>(() => sourceStream.WriteByte(1));
    }

    [Fact]
    public async Task DisposeAsync_ClosesSourceStream()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Act
        await wrapper.DisposeAsync();

        // Assert - source stream should be closed
        Assert.Throws<ObjectDisposedException>(() => sourceStream.WriteByte(1));
    }

    [Fact]
    public void Seek_ThrowsNotSupportedException()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => wrapper.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void SetLength_ThrowsNotSupportedException()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => wrapper.SetLength(100));
    }

    [Fact]
    public void Write_ThrowsNotSupportedException()
    {
        // Arrange
        var sourceStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });
        var buffer = new byte[5];

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => wrapper.Write(buffer, 0, 5));
    }

    [Fact]
    public async Task ReadAsync_Memory_WithValidData_ReadsCorrectly()
    {
        // Arrange
        var sourceData = new byte[] { 1, 2, 3, 4, 5 };
        var sourceStream = new MemoryStream(sourceData);
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });
        var buffer = new Memory<byte>(new byte[5]);

        // Act
        int read = await wrapper.ReadAsync(buffer, CancellationToken.None);

        // Assert
        Assert.Equal(5, read);
        Assert.Equal(sourceData, buffer.ToArray());
    }

    [Fact]
    public async Task ReadAsync_EndOfStream_ReturnsZero()
    {
        // Arrange
        var sourceData = new byte[] { 1, 2, 3 };
        var sourceStream = new MemoryStream(sourceData);
        var wrapper = new IcyStreamWrapper(sourceStream, 8162, () => new byte[] { 0 });
        var buffer = new byte[10];

        // Read all data
        await wrapper.ReadAsync(buffer, 0, 10, CancellationToken.None);

        // Act - try to read again at end
        int read = await wrapper.ReadAsync(buffer, 0, 10, CancellationToken.None);

        // Assert
        Assert.Equal(0, read);
    }

    // Helper: Non-readable stream for testing
    private class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
