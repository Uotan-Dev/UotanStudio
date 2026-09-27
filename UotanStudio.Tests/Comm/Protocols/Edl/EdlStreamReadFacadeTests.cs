using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlStreamReadFacadeTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Ready = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";
    private const string Complete = "<data><response value=\"ACK\" rawmode=\"false\"/></data>";

    [Fact]
    public async Task ReadPartitionToStreamAsync_FullPayload_MeasuresLengthHashAndAcknowledgement()
    {
        var payload = new byte[8192];
        new Random(11).NextBytes(payload);
        using var transport = ReadTransport(payload);
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, 16,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8192, result.BytesWritten);
        Assert.Equal(8192, result.ExpectedBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Sha256Hex);
        Assert.True(result.DeviceAcknowledged);
        Assert.Equal(payload, destination.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(1024)]
    public async Task ReadPartitionToStreamAsync_FragmentedWire_WritesInBoundedChunksWithoutWholeReadBuffer(int fragmentSize)
    {
        var payload = new byte[8192];
        new Random(23).NextBytes(payload);
        byte[] wire = [.. Encoding.UTF8.GetBytes(Ready), .. payload, .. Encoding.UTF8.GetBytes(Complete)];
        using var transport = new ScriptedCommTransport();
        for (var offset = 0; offset < wire.Length; offset += fragmentSize)
        {
            transport.Enqueue(wire.AsSpan(offset, Math.Min(fragmentSize, wire.Length - offset)).ToArray());
        }

        using var protocol = Protocol(transport);
        using var destination = new ChunkRecordingStream();

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, 16,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8192, result.BytesWritten);
        Assert.Equal(payload, destination.Totaled());
        Assert.True(destination.WriteSizes.Count > 1);
        Assert.Equal(8192, destination.WriteSizes.Sum());
        Assert.DoesNotContain(8192, destination.WriteSizes);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_NonSeekableDestination_Succeeds()
    {
        var payload = new byte[4096];
        new Random(31).NextBytes(payload);
        using var transport = ReadTransport(payload);
        using var protocol = Protocol(transport);
        using var destination = new NonSeekableWriteStream();

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, 8,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(destination.WasSeekCalled);
        Assert.Equal(4096, result.BytesWritten);
        Assert.Equal(payload, destination.Totaled());
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_ProgressReportsCumulativeMonotonicBytes()
    {
        var payload = new byte[8192];
        new Random(41).NextBytes(payload);
        using var transport = ReadTransport(payload, fragment: 2048);
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();
        var reports = new List<long>();
        var progress = new CollectingProgress(reports);

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, 16, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8192, result.BytesWritten);
        Assert.Equal(8192, reports[^1]);
        Assert.True(reports.Count > 1);
        for (var index = 1; index < reports.Count; index++)
        {
            Assert.True(reports[index] >= reports[index - 1]);
        }
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_MatchingExpectedHash_ReturnsResult()
    {
        var payload = new byte[2048];
        new Random(51).NextBytes(payload);
        using var transport = ReadTransport(payload);
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, 4,
            expectedSha256Hex: Convert.ToHexStringLower(SHA256.HashData(payload)).ToUpperInvariant(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2048, result.BytesWritten);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_ExpectedHashContradiction_ThrowsResourceMismatchAndKeepsDestinationUnpublished()
    {
        var payload = new byte[2048];
        new Random(61).NextBytes(payload);
        using var transport = ReadTransport(payload);
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<EdlException>(() => protocol.ReadPartitionToStreamAsync(destination, 0, 4,
            expectedSha256Hex: new string('a', 64), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.OnTargetVerificationFailed, error.ErrorCode);
        Assert.Equal(EdlFailureKind.ResourceMismatch, error.FailureKind);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_ShortPayload_FailsWithExecutionUnknownAndPartialDestination()
    {
        var payload = new byte[256];
        new Random(71).NextBytes(payload);
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ready);
        transport.Enqueue(payload);
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<EdlException>(() => protocol.ReadPartitionToStreamAsync(destination, 0, 4,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlFailureKind.ExecutionUnknown, error.FailureKind);
        Assert.Equal(payload, destination.ToArray());
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_CompleteNak_FailsWithoutResult()
    {
        var payload = new byte[512];
        new Random(81).NextBytes(payload);
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ready);
        transport.Enqueue(payload);
        transport.Enqueue("<data><response value=\"NAK\" rawmode=\"false\"/></data>");
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<EdlException>(() => protocol.ReadPartitionToStreamAsync(destination, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(payload, destination.ToArray());
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_PreCancelled_WritesNothingToTransportOrDestination()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = Protocol(transport);
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(destination,
            0, 1, cancellationToken: cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_MidTransferCancellation_KeepsPartialBytesAndMarksCancelledAfterTransfer()
    {
        var payload = new byte[8192];
        new Random(91).NextBytes(payload);
        using var transport = ReadTransport(payload, fragment: 2048);
        using var protocol = Protocol(transport);
        using var cancellation = new CancellationTokenSource();
        using var destination = new CancellingOnThirdWriteStream(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(destination,
            0, 16, cancellationToken: cancellation.Token));

        Assert.True(destination.Totaled().Length is > 0 and < 8192);
        Assert.Equal(EdlFailureKind.CancelledAfterTransfer, protocol.LastFailureKind);
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task SessionReadPartitionToStreamAsync_DelegatesFacadeWithMeasuredEvidence()
    {
        var payload = new byte[1024];
        new Random(101).NextBytes(payload);
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport);
        EnqueueDoneResponse(transport);
        transport.Enqueue("<data><response value=\"ACK\" MaxPayloadSizeToTargetInBytes=\"4096\"/></data>");
        transport.Enqueue(Ready);
        transport.Enqueue(payload);
        transport.Enqueue(Complete);
        using var session = new EdlSession(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });
        using var programmer = new MemoryStream("abc"u8.ToArray());
        await session.InitializeAsync(new EdlProgrammerSelection
        {
            Decision = EdlProgrammerMatchDecision.Matched,
            Descriptor = new EdlProgrammerDescriptor
            {
                FileName = "prog.bin",
                Sha256Hex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            },
            PerCandidateReasons = []
        }, programmer, null, TestContext.Current.CancellationToken);

        using var destination = new MemoryStream();
        var result = await session.ReadPartitionToStreamAsync(destination, 0, 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1024, result.BytesWritten);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Sha256Hex);
        Assert.True(result.DeviceAcknowledged);
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(AbcSha256, session.Capabilities!.ResourceSha256Hex);
    }

    private static ScriptedCommTransport ReadTransport(byte[] payload, int fragment = 0)
    {
        var transport = new ScriptedCommTransport();
        if (fragment <= 0)
        {
            transport.Enqueue(Ready);
            transport.Enqueue(payload);
            transport.Enqueue(Complete);
            return transport;
        }

        byte[] wire = [.. Encoding.UTF8.GetBytes(Ready), .. payload, .. Encoding.UTF8.GetBytes(Complete)];
        for (var offset = 0; offset < wire.Length; offset += fragment)
        {
            transport.Enqueue(wire.AsSpan(offset, Math.Min(fragment, wire.Length - offset)).ToArray());
        }

        return transport;
    }

    private static FirehoseProtocol Protocol(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, ResponseTimeoutSeconds = 1 }, 50);

    private sealed class CollectingProgress(List<long> sink) : IProgress<long>
    {
        public void Report(long value) => sink.Add(value);
    }

    private sealed class ChunkRecordingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public List<int> WriteSizes { get; } = [];

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteSizes.Add(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteSizes.Add(buffer.Length);
            _inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteSizes.Add(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteSizes.Add(buffer.Length);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public byte[] Totaled() => _inner.ToArray();
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public bool WasSeekCalled { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            WasSeekCalled = true;
            throw new NotSupportedException();
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        public byte[] Totaled() => _inner.ToArray();
    }

    private sealed class CancellingOnThirdWriteStream(CancellationTokenSource cancellation) : Stream
    {
        private readonly MemoryStream _inner = new();
        private int _writes;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsyncCore(buffer.AsMemory(offset, count), cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(WriteAsyncCore(buffer, cancellationToken));

        private Task WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) >= 3)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return _inner.WriteAsync(buffer, cancellationToken).AsTask();
        }

        public byte[] Totaled() => _inner.ToArray();
    }

    private static void EnqueueHello(ScriptedCommTransport transport)
    {
        var hello = new SaharaHelloPacket
        {
            Command = SaharaCommand.Hello,
            Length = (uint)Marshal.SizeOf<SaharaHelloPacket>(),
            Version = EdlConstants.SaharaVersion,
            MinVersion = 1,
            MaxCommandLength = 3,
            Mode = SaharaMode.ImageTxPending
        };
        transport.Enqueue(StructToBytes(hello));
    }

    private static void EnqueueEndOfImage(ScriptedCommTransport transport)
    {
        var packet = new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = (uint)Marshal.SizeOf<SaharaEndOfImagePacket>(),
            ImageId = (uint)SaharaImageId.Firehose,
            Status = 0
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueDoneResponse(ScriptedCommTransport transport)
    {
        var packet = new SaharaDoneResponsePacket
        {
            Command = SaharaCommand.DoneResponse,
            Length = (uint)Marshal.SizeOf<SaharaDoneResponsePacket>(),
            Status = 1
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), fDeleteOld: false);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }
}