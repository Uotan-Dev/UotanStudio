using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Writing;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Tests.DD;

internal sealed class FlashTestDirectory : IDisposable
{
    internal FlashTestDirectory()
    {
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "uotan-dd-tests", Guid.NewGuid().ToString("N"));

    internal string Combine(string name) => Path.Combine(Root, name);

    internal string WriteFile(string name, byte[] bytes)
    {
        var path = Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class FlashTestData
{
    internal static byte[] CreateImageData(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    internal static FlashDrive CreateDrive(
        string path,
        long size,
        bool isSystem = false,
        bool isReadOnly = false,
        IReadOnlyList<string>? mountPoints = null)
    {
        return new FlashDrive
        {
            DevicePath = path,
            DisplayName = Path.GetFileName(path),
            Size = size,
            IsRemovable = !isSystem,
            IsReadOnly = isReadOnly,
            IsSystem = isSystem,
            MountPoints = mountPoints ?? [],
        };
    }

    internal static string ComputeSha256(ReadOnlySpan<byte> data)
    {
        using var accumulator = Sha256.CreateAccumulator();
        accumulator.Append(data);
        return Convert.ToHexStringLower(accumulator.GetHashAndReset());
    }
}

internal sealed class RecordingProgress : IProgress<FlashProgress>
{
    internal List<FlashProgress> Reports { get; } = [];

    public void Report(FlashProgress value) => Reports.Add(value);
}

internal sealed class CancelOnWriteProgress : IProgress<FlashProgress>
{
    private readonly CancellationTokenSource _source;
    private readonly long _threshold;

    internal CancelOnWriteProgress(CancellationTokenSource source, long threshold)
    {
        _source = source;
        _threshold = threshold;
    }

    public void Report(FlashProgress value)
    {
        if (value.Stage == FlashStage.Writing && value.BytesProcessed >= _threshold)
        {
            _source.Cancel();
        }
    }
}

internal sealed class MismatchDiskTargetFactory : IDiskTargetFactory
{
    internal static MismatchDiskTargetFactory Instance { get; } = new();

    public IDiskTarget Create(FlashDrive drive) => new MismatchDiskTarget(drive);
}

internal sealed class MismatchDiskTarget : IDiskTarget
{
    private readonly CorruptingStream _stream = new();

    internal MismatchDiskTarget(FlashDrive drive)
    {
        Drive = drive;
    }

    public FlashDrive Drive { get; }

    public Stream Stream => _stream;

    public Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class AlignedDiskTargetFactory : IDiskTargetFactory
{
    internal const int AlignmentBytes = 4096;

    internal AlignedDiskTarget? Target { get; private set; }

    public IDiskTarget Create(FlashDrive drive)
    {
        var target = new AlignedDiskTarget(drive, AlignmentBytes);
        Target = target;
        return target;
    }
}

internal sealed class AlignedDiskTarget : IDiskTarget
{
    private readonly SectorAlignedStream _stream;

    internal AlignedDiskTarget(FlashDrive drive, int alignment)
    {
        Drive = drive;
        ChunkAlignmentBytes = alignment;
        _stream = new SectorAlignedStream(alignment);
    }

    public FlashDrive Drive { get; }

    public Stream Stream => _stream;

    public int ChunkAlignmentBytes { get; }

    internal byte[] WrittenBytes => _stream.ToArray();

    public Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SectorAlignedStream : MemoryStream
{
    private readonly int _alignment;

    internal SectorAlignedStream(int alignment) => _alignment = alignment;

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureAligned(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureAligned(buffer.Length);
        base.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureAligned(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureAligned(count);
        return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        EnsureAligned(buffer.Length);
        return base.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureAligned(buffer.Length);
        return base.ReadAsync(buffer, cancellationToken);
    }

    private void EnsureAligned(int length)
    {
        if (length % _alignment != 0 || Position % _alignment != 0)
        {
            throw new IOException($"A raw target requires {_alignment}-byte aligned reads and writes.");
        }
    }
}

internal sealed class CorruptingStream : Stream
{
    private readonly MemoryStream _written = new();
    private readonly MemoryStream _readback = new();
    private bool _readbackReady;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => Current.Length;

    public override long Position
    {
        get => Current.Position;
        set => Current.Position = value;
    }

    private Stream Current => _readbackReady ? _readback : _written;

    public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _written.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureReadback();
        return _readback.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        EnsureReadback();
        return _readback.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureReadback();
        return ValueTask.FromResult(_readback.Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureReadback();
        return _readback.Seek(offset, origin);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    private void EnsureReadback()
    {
        if (_readbackReady)
        {
            return;
        }

        var bytes = _written.ToArray();
        if (bytes.Length > 0)
        {
            bytes[0] ^= 0xFF;
        }

        _readback.Write(bytes, 0, bytes.Length);
        _readback.Position = 0;
        _readbackReady = true;
    }
}