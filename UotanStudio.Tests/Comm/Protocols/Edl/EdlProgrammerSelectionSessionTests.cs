using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlProgrammerSelectionSessionTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string WrongSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ConfigureAck = "<data><response value=\"ACK\"/></data>";

    [Fact]
    public async Task InitializeAsync_MatchedSelectionWithCorrectHash_UploadsAndConfiguresWithEvidence()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, status: 0);
        EnqueueDoneResponse(transport, status: 1);
        transport.Enqueue(Encoding.UTF8.GetBytes(ConfigureAck));
        using var session = CreateSession(transport);
        using var image = new MemoryStream("abc"u8.ToArray());

        await session.InitializeAsync(Matched(AbcSha256), image, null, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(EdlLinkStage.FirehoseReady, session.LinkStage);
        Assert.Equal(new[] { "programmer-selection", "programmer-hash-preflight" },
            session.InitializationEvidence.Select(static evidence => evidence.Phase));
        Assert.All(session.InitializationEvidence, evidence =>
            Assert.Equal(CommandOutcomeKind.Completed, evidence.Outcome));
        Assert.Equal(AbcSha256, session.InitializationEvidence[1].ResourceSha256Hex);
        Assert.Contains("<configure", Encoding.UTF8.GetString(transport.Writes[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_MeasuredHashContradictsDescriptor_FaultsBeforeAnyTransportWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        using var image = new MemoryStream("abc"u8.ToArray());

        var error = await Assert.ThrowsAsync<EdlException>(() => session.InitializeAsync(
            Matched(WrongSha256), image, null, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ProgrammerMismatch, error.ErrorCode);
        Assert.Empty(transport.Writes);
        Assert.Equal(EdlLinkStage.Idle, session.LinkStage);
        Assert.Equal(CommandOutcomeKind.Rejected,
            Assert.Single(session.InitializationEvidence, evidence =>
                evidence.Phase == "programmer-hash-preflight").Outcome);
        Assert.Equal(AbcSha256, session.InitializationEvidence[^1].ResourceSha256Hex);
    }

    [Fact]
    public async Task InitializeAsync_SelectionRecordHashContradiction_FaultsBeforeAnyTransportWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        using var image = new MemoryStream("abc"u8.ToArray());
        var selection = new EdlProgrammerSelection
        {
            Decision = EdlProgrammerMatchDecision.Matched,
            Descriptor = Descriptor(AbcSha256),
            ActualSha256Hex = WrongSha256,
            PerCandidateReasons = []
        };

        var error = await Assert.ThrowsAsync<EdlException>(() => session.InitializeAsync(
            selection, image, null, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ProgrammerMismatch, error.ErrorCode);
        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData("no-match")]
    [InlineData("ambiguous")]
    [InlineData("needs-explicit")]
    public async Task InitializeAsync_NonMatchedDecision_FaultsWithDistinctCodeBeforeAnyWrite(string variant)
    {
        var (decision, expectedCode) = variant switch
        {
            "no-match" => (EdlProgrammerMatchDecision.NoMatch, EdlErrorCode.ProgrammerMismatch),
            "ambiguous" => (EdlProgrammerMatchDecision.Ambiguous, EdlErrorCode.ProgrammerMismatch),
            _ => (EdlProgrammerMatchDecision.NeedsExplicitSelection, EdlErrorCode.NeedsProgrammerSelection)
        };
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        using var image = new MemoryStream("abc"u8.ToArray());
        var selection = new EdlProgrammerSelection
        {
            Decision = decision,
            Descriptor = Descriptor(AbcSha256),
            PerCandidateReasons = []
        };

        var error = await Assert.ThrowsAsync<EdlException>(() => session.InitializeAsync(
            selection, image, null, TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.ErrorCode);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task InitializeAsync_NonSeekableStream_ThrowsArgumentExceptionBeforeAnyWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        await using var image = new NoSeekStream("abc"u8.ToArray());

        await Assert.ThrowsAsync<ArgumentException>(() => session.InitializeAsync(
            Matched(AbcSha256), image, null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(CommandOutcomeKind.Rejected,
            Assert.Single(session.InitializationEvidence, evidence =>
                evidence.Phase == "programmer-hash-preflight").Outcome);
    }

    [Fact]
    public async Task InitializeAsync_TruncatedStreamAgainstFullDescriptorHash_FaultsBeforeAnyWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        using var full = new MemoryStream("abcdefgh"u8.ToArray());
        var fullHash = EdlProgrammerHash.ComputeSha256Hex(full, TestContext.Current.CancellationToken);
        using var truncated = new MemoryStream("abcde"u8.ToArray());

        var error = await Assert.ThrowsAsync<EdlException>(() => session.InitializeAsync(
            Matched(fullHash), truncated, null, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ProgrammerMismatch, error.ErrorCode);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task InitializeAsync_PreCancelledToken_CancelsBeforeAnyWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport);
        using var image = new MemoryStream("abc"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InitializeAsync(
            Matched(AbcSha256), image, null, cancellation.Token));

        Assert.Empty(transport.Writes);
    }


    private static EdlSession CreateSession(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration
        {
            AutoDetectSectorSize = false,
            SectorSize = 512,
            ConfigureFirstAttemptTimeoutMs = 5000
        });

    private static EdlProgrammerDescriptor Descriptor(string sha256Hex) => new()
    {
        FileName = "prog_firehose.bin",
        Sha256Hex = sha256Hex
    };

    private static EdlProgrammerSelection Matched(string descriptorSha256Hex) => new()
    {
        Decision = EdlProgrammerMatchDecision.Matched,
        Descriptor = Descriptor(descriptorSha256Hex),
        PerCandidateReasons = []
    };

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

    private static void EnqueueEndOfImage(ScriptedCommTransport transport, uint status)
    {
        var packet = new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = (uint)Marshal.SizeOf<SaharaEndOfImagePacket>(),
            ImageId = (uint)SaharaImageId.Firehose,
            Status = status
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueDoneResponse(ScriptedCommTransport transport, uint status)
    {
        var packet = new SaharaDoneResponsePacket
        {
            Command = SaharaCommand.DoneResponse,
            Length = (uint)Marshal.SizeOf<SaharaDoneResponsePacket>(),
            Status = status
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

    private sealed class NoSeekStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}