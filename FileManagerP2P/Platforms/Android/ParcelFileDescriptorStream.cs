using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.OS;
using Java.IO;

namespace FileManagerP2P.Platforms.Android
{
    public class ParcelFileDescriptorStream : Stream
    {
        private readonly ParcelFileDescriptor _pfd;
        private readonly FileInputStream? _inputStream;
        private readonly FileOutputStream? _outputStream;
        private readonly string _mode;
        private bool _disposed;

        public ParcelFileDescriptorStream(ParcelFileDescriptor pfd, string mode)
        {
            _pfd = pfd ?? throw new ArgumentNullException(nameof(pfd));
            _mode = mode ?? throw new ArgumentNullException(nameof(mode));

            if (mode.Contains('r'))
            {
                _inputStream = new FileInputStream(_pfd.FileDescriptor);
            }

            if (mode.Contains('w'))
            {
                _outputStream = new FileOutputStream(_pfd.FileDescriptor);
            }

            if (_inputStream == null && _outputStream == null)
            {
                throw new ArgumentException("Mode must contain 'r' or 'w'", nameof(mode));
            }
        }

        public override bool CanRead => _inputStream != null && !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => _outputStream != null && !_disposed;
        public override long Length => _pfd?.StatSize ?? 0;

        public override long Position
        {
            get => _inputStream?.Channel?.Position() ?? _outputStream?.Channel?.Position() ?? 0;
            set
            {
                ThrowIfDisposed();
                if (_inputStream?.Channel != null)
                {
                    _inputStream.Channel.Position(value);
                }
                if (_outputStream?.Channel != null)
                {
                    _outputStream.Channel.Position(value);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(ParcelFileDescriptorStream));
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _inputStream?.Dispose();
                    _outputStream?.Dispose();
                    _pfd?.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _inputStream?.Dispose();
                _outputStream?.Dispose();
                _pfd?.Dispose();
                _disposed = true;
                // This prevents derived types with finalizers from needing to re-implement IDisposable
                GC.SuppressFinalize(this);
            }
            await base.DisposeAsync();
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            _outputStream?.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            _outputStream?.Flush();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (_inputStream == null)
                throw new NotSupportedException("Stream was not opened for reading");

            return _inputStream.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_inputStream == null)
                throw new NotSupportedException("Stream was not opened for reading");

            return await Task.Run(() => _inputStream.Read(buffer, offset, count), cancellationToken);
        }

        // Override memory-based ReadAsync for improved performance
        // Override memory-based ReadAsync for improved performance
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_inputStream == null)
                throw new NotSupportedException("Stream was not opened for reading");

            // Get a span from the buffer for direct access
            Span<byte> span = buffer.Span;

            // For small reads, use a synchronous approach to avoid Task overhead
            if (buffer.Length <= 4096) // 4KB threshold
            {
                try
                {
                    // Create a managed byte array that can be pinned for native access
                    byte[] tempBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
                    try
                    {
                        int bytesRead = _inputStream.Read(tempBuffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            // Copy data to the destination span
                            tempBuffer.AsSpan(0, bytesRead).CopyTo(span);
                        }
                        return new ValueTask<int>(bytesRead);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(tempBuffer);
                    }
                }
                catch (Exception ex)
                {
                    return new ValueTask<int>(Task.FromException<int>(ex));
                }
            }

            // For larger reads or when cancellation is requested, use the asynchronous path
            // Use direct memory access when possible
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
            {
                return new ValueTask<int>(ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
            }
            else
            {
                // Fallback for when direct access is not possible
                return new ValueTask<int>(ReadArrayPoolAsyncCore(buffer, cancellationToken));
            }
        }

        // Helper method for the async fallback path
        private async Task<int> ReadArrayPoolAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                // Change to use Memory<byte> overload instead of array-based overload
                int bytesRead = await Task.Run(() => _inputStream!.Read(rentedBuffer, 0, buffer.Length), cancellationToken)
           .ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    rentedBuffer.AsSpan(0, bytesRead).CopyTo(buffer.Span);
                }
                return bytesRead;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }


        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();

            var channel = _inputStream?.Channel ?? _outputStream?.Channel
            ?? throw new NotSupportedException("Cannot seek this stream");


            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => channel.Position() + offset,
                SeekOrigin.End => _pfd.StatSize + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };


            channel.Position(position);
            return position;
        }

        public override void SetLength(long value)
        {
            ThrowIfDisposed();
            throw new NotSupportedException("Cannot change the length of this stream");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (_outputStream == null)
                throw new NotSupportedException("Stream was not opened for writing");

            _outputStream.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (_outputStream == null)
                throw new NotSupportedException("Stream was not opened for writing");

            await Task.Run(() => _outputStream.Write(buffer, offset, count), cancellationToken);
        }

        // Override memory-based WriteAsync for improved performance
        // Override memory-based WriteAsync for improved performance
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_outputStream == null)
                throw new NotSupportedException("Stream was not opened for writing");

            // For small writes, use a synchronous approach to avoid Task overhead
            if (buffer.Length <= 4096) // 4KB threshold
            {
                try
                {
                    byte[] tempBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
                    try
                    {
                        buffer.Span.CopyTo(tempBuffer);
                        _outputStream.Write(tempBuffer, 0, buffer.Length);
                        return new ValueTask();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(tempBuffer);
                    }
                }
                catch (Exception ex)
                {
                    return new ValueTask(Task.FromException(ex));
                }
            }

            // For larger writes or when cancellation is requested, use the asynchronous path
            // Use direct memory access when possible
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
            {
                return new ValueTask(WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
            }
            else
            {
                // Fallback for when direct access is not possible
                return new ValueTask(WriteArrayPoolAsyncCore(buffer, cancellationToken));
            }
        }

        // Helper method for the async fallback path
        private async Task WriteArrayPoolAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rentedBuffer);
                await Task.Run(() => _outputStream!.Write(rentedBuffer, 0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

    }
}
