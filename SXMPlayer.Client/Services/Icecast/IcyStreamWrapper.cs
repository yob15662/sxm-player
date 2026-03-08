using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// Stream wrapper that injects ICY metadata blocks every N bytes of audio data.
/// This provides a Stream-based API for ICY metadata injection, useful for legacy code
/// that expects a stream interface. Internally respects frame boundaries via callbacks.
/// Non-seekable, read-only.
/// </summary>
public sealed class IcyStreamWrapper : Stream
{
    private readonly Stream _source;
    private readonly int _icyMetaInt;
    private readonly Func<byte[]> _metadataFactory;

    private int _bytesUntilMeta;
    private Memory<byte>? _metadataBuffer;
    private int _metadataOffset;
    private bool _disposed;

    public IcyStreamWrapper(Stream source, int icyMetaInt, Func<byte[]> metadataFactory)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!source.CanRead) throw new ArgumentException("Source stream must be readable", nameof(source));
        if (icyMetaInt <= 0) throw new ArgumentOutOfRangeException(nameof(icyMetaInt));
        _icyMetaInt = icyMetaInt;
        _metadataFactory = metadataFactory ?? throw new ArgumentNullException(nameof(metadataFactory));
        _bytesUntilMeta = _icyMetaInt;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if ((uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(count));
        if (_disposed) throw new ObjectDisposedException(nameof(IcyStreamWrapper));

        return ReadCoreAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IcyStreamWrapper));
        if (MemoryMarshal.TryGetArray(destination, out ArraySegment<byte> seg))
        {
            return new ValueTask<int>(ReadCoreAsync(seg.Array!, seg.Offset, seg.Count, cancellationToken));
        }
        // Fallback for non-array-backed memory
        var arr = ArrayPool<byte>.Shared.Rent(destination.Length);
        return new ValueTask<int>(ReadIntoMemoryFallbackAsync(arr, destination, cancellationToken));
    }

    private async Task<int> ReadIntoMemoryFallbackAsync(byte[] temp, Memory<byte> destination, CancellationToken cancellationToken)
    {
        try
        {
            int read = await ReadCoreAsync(temp, 0, destination.Length, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                temp.AsMemory(0, read).CopyTo(destination);
            }
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int totalWritten = 0;

        while (totalWritten < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If we are currently emitting a metadata block, write from it first
            if (_metadataBuffer is not null)
            {
                var metaSpan = _metadataBuffer.Value.Span;
                int metaRemaining = metaSpan.Length - _metadataOffset;
                int toCopy = Math.Min(metaRemaining, count - totalWritten);
                metaSpan.Slice(_metadataOffset, toCopy).CopyTo(buffer.AsSpan(offset + totalWritten, toCopy));
                _metadataOffset += toCopy;
                totalWritten += toCopy;

                if (_metadataOffset >= metaSpan.Length)
                {
                    // Finished metadata, reset state and continue with audio
                    _metadataBuffer = null;
                    _metadataOffset = 0;
                    _bytesUntilMeta = _icyMetaInt;
                }

                // If we filled the caller's buffer with metadata, return now
                if (totalWritten >= count)
                {
                    break;
                }

                // Otherwise continue to fetch audio bytes
            }

            // If it's time to inject new metadata and we are not already emitting one
            if (_bytesUntilMeta == 0 && _metadataBuffer is null)
            {
                _metadataBuffer = new Memory<byte>(_metadataFactory());
                _metadataOffset = 0;
                continue; // loop will write metadata on next iteration
            }

            // Read audio bytes up to either the requested count or until next metadata injection
            int toRead = Math.Min(_bytesUntilMeta, count - totalWritten);
            if (toRead == 0)
            {
                // The caller requested zero additional audio bytes but we still owe a metadata block
                // Prepare metadata for the next iteration
                _metadataBuffer = new Memory<byte>(_metadataFactory());
                _metadataOffset = 0;
                continue;
            }

            int read = await _source.ReadAsync(buffer.AsMemory(offset + totalWritten, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                // End of source stream
                break;
            }

            totalWritten += read;
            _bytesUntilMeta -= read;

            if (_bytesUntilMeta == 0)
            {
                // Next loop iteration will emit metadata
            }
        }

        return totalWritten;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _source.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _source.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
