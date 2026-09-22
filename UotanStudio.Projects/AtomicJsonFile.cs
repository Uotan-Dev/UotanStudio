using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UotanStudio.Services.Projects;

internal static class AtomicJsonFile
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<bool> WriteIfChangedAsync<T>(string path, T value, JsonTypeInfo<T> type,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var staged = new StagedJsonStream(temporary);
            await JsonSerializer.SerializeAsync(staged, value, type, cancellationToken).ConfigureAwait(false);
            staged.Position = 0;
            if (File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 * 1024
                && await SameTextAsync(path, staged, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            await staged.PrepareFileAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { Gate.Release(); }
        }
    }

    private static async Task<bool> SameTextAsync(string path, Stream staged, CancellationToken token)
    {
        // Retain ReadAllText's BOM-aware comparison, including existing UTF-16 project files.
        using var existing = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan));
        using var generated = new StreamReader(staged, leaveOpen: true);
        var left = ArrayPool<char>.Shared.Rent(8192);
        var right = ArrayPool<char>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var count = await existing.ReadBlockAsync(left.AsMemory(0, 8192), token).ConfigureAwait(false);
                var other = await generated.ReadBlockAsync(right.AsMemory(0, 8192), token).ConfigureAwait(false);
                if (count != other || !left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, other)))
                {
                    return false;
                }
                if (count == 0)
                {
                    return true;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(left, clearArray: true);
            ArrayPool<char>.Shared.Return(right, clearArray: true);
        }
    }

    /// <summary>Small snapshots stay in memory; larger snapshots spill beside the atomic destination.</summary>
    private sealed class StagedJsonStream(string temporary) : Stream
    {
        private const int MemoryLimit = 128 * 1024;
        private const int MaximumBytes = 32 * 1024 * 1024;
        private Stream _content = new MemoryStream();
        private bool _fileBacked;
        public override bool CanRead => _content.CanRead;
        public override bool CanSeek => _content.CanSeek;
        public override bool CanWrite => _content.CanWrite;
        public override long Length => _content.Length;
        public override long Position { get => _content.Position; set => _content.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _content.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _content.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => _content.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _content.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(buffer.Length);
            if (!_fileBacked && Length + buffer.Length > MemoryLimit)
            {
                var file = CreateFile();
                try
                {
                    _content.Position = 0;
                    _content.CopyTo(file);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
                _content.Dispose();
                _content = file;
                _fileBacked = true;
            }
            _content.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckLength(buffer.Length);
            if (!_fileBacked && Length + buffer.Length > MemoryLimit)
            {
                var file = CreateFile();
                try
                {
                    _content.Position = 0;
                    await _content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                await _content.DisposeAsync().ConfigureAwait(false);
                _content = file;
                _fileBacked = true;
            }
            await _content.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PrepareFileAsync(CancellationToken token)
        {
            if (_fileBacked)
            {
                await _content.FlushAsync(token).ConfigureAwait(false);
                await _content.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await using var file = CreateFile();
                _content.Position = 0;
                await _content.CopyToAsync(file, token).ConfigureAwait(false);
            }
        }

        private FileStream CreateFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(temporary))!);
            return new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        private void CheckLength(int count)
        {
            if (Length > MaximumBytes - count)
            {
                throw new InvalidDataException("Project state exceeds 32 MiB.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _content.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _content.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}