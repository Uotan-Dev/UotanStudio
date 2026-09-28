using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PatchFormatProbeTests
{
    [Theory]
    [InlineData("{\"enabled\":true}", PatchFormatKind.Json)]
    [InlineData("false", PatchFormatKind.Json)]
    [InlineData("<root><item /></root>", PatchFormatKind.Xml)]
    public void Read_ConfigurationContent_ValidatesAndRestoresPosition(string text, PatchFormatKind expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        stream.Position = 2;
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Matches(expected, PatchFormatEvidence.Structure));
        Assert.Equal(2, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_UnicodeWithLongWhitespace_RecognizesContentBeyondProbe(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        using var stream = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes(new string(' ', 5000) + "{\"中文\":null}")]);
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken)
            .Matches(PatchFormatKind.Json, PatchFormatEvidence.Structure));
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"x\":")]
    [InlineData("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///must-not-open'>]><x>&a;</x>")]
    [InlineData("<root>")]
    public void Read_InvalidConfiguration_RejectsEvenSignatureRequirement(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Rejected);
        Assert.False(result.Matches(result.Format, PatchFormatEvidence.Signature));
    }

    [Theory]
    [InlineData("414e44524f494421", PatchFormatKind.Boot)]
    [InlineData("564e4452424f4f54", PatchFormatKind.VendorBoot)]
    [InlineData("43724155", PatchFormatKind.Payload)]
    [InlineData("d00dfeed", PatchFormatKind.Dtb)]
    [InlineData("d7b7ab1e", PatchFormatKind.Dtbo)]
    [InlineData("6465780a30333500", PatchFormatKind.Dex)]
    [InlineData("504b0304", PatchFormatKind.Zip)]
    [InlineData("303730373031", PatchFormatKind.Cpio)]
    [InlineData("1f8b08", PatchFormatKind.Gzip)]
    [InlineData("fd377a585a00", PatchFormatKind.Xz)]
    [InlineData("28b52ffd", PatchFormatKind.Zstd)]
    [InlineData("04224d18", PatchFormatKind.Lz4)]
    [InlineData("4253444946463430", PatchFormatKind.Bsdiff)]
    [InlineData("524b4657", PatchFormatKind.Rockchip)]
    [InlineData("4f505300", PatchFormatKind.Ops)]
    [InlineData("4f50504f454e435259505421", PatchFormatKind.Ozip)]
    [InlineData("4e545049", PatchFormatKind.Ntpi)]
    public void Read_RecognizedSignature_DoesNotClaimStructuralValidation(string hex, PatchFormatKind expected)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Format);
        Assert.Equal(PatchFormatEvidence.Signature, result.Evidence);
        Assert.False(result.Matches(expected, PatchFormatEvidence.Structure));
    }

    [Theory]
    [InlineData(1080, "53ef", PatchFormatKind.ExtFamily)]
    [InlineData(1024, "e2e1f5e0", PatchFormatKind.Erofs)]
    [InlineData(1024, "1020f5f2", PatchFormatKind.F2fs)]
    [InlineData(8, "5619b527", PatchFormatKind.Amlogic)]
    public void Read_OffsetSignature_UsesContentOffsets(int offset, string hex, PatchFormatKind expected)
    {
        var bytes = new byte[4096];
        Convert.FromHexString(hex).CopyTo(bytes, offset);
        using var stream = new MemoryStream(bytes);
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Format);
        Assert.Equal(PatchFormatEvidence.Signature, result.Evidence);
    }

    [Theory]
    [InlineData("7f454c46", PatchFormatKind.Elf)]
    [InlineData("3aff26ed", PatchFormatKind.Sparse)]
    [InlineData("41564230", PatchFormatKind.Vbmeta)]
    public void Read_TruncatedKnownStructure_RejectsMagicOnlyInput(string hex, PatchFormatKind expected)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Format);
        Assert.True(result.Rejected);
        Assert.False(result.Matches(expected, PatchFormatEvidence.Signature));
    }

    [Fact]
    public void Read_Elf64HeaderAndOutOfRangeTable_DistinguishesValidMetadata()
    {
        var bytes = new byte[64];
        "\u007fELF"u8.CopyTo(bytes);
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);
        using var stream = new MemoryStream(bytes);
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken)
            .Matches(PatchFormatKind.Elf, PatchFormatEvidence.Structure));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 1);
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken).Rejected);
    }

    [Fact]
    public void Read_AospSparseFixture_ValidatesChunkBoundariesAndTotals()
    {
        var bytes = Convert.FromHexString("3aff26ed010000001c000c0000100000010000000100000000000000c3ca0000010000000c000000");
        using var stream = new MemoryStream(bytes);
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken)
            .Matches(PatchFormatKind.Sparse, PatchFormatEvidence.Structure));
        bytes[32] = 2;
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken).Rejected);
    }

    [Fact]
    public void Read_VbmetaMetadata_DoesNotVerifyTrustAndRejectsOutOfRangeBlocks()
    {
        var bytes = new byte[256];
        "AVB0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 1);
        using var stream = new MemoryStream(bytes);
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Matches(PatchFormatKind.Vbmeta, PatchFormatEvidence.Structure));
        Assert.Equal("AvbDescriptors", result.Detail);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(12), 64);
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken).Rejected);
    }

    [Fact]
    public void Read_IniHint_RequiresExplicitDialectAndRejectsDuplicateKeys()
    {
        using var valid = new MemoryStream("[service]\nenabled=true"u8.ToArray());
        Assert.True(PatchFormatProbe.Read(valid, PatchFormatKind.Ini, TestContext.Current.CancellationToken)
            .Matches(PatchFormatKind.Ini, PatchFormatEvidence.Structure));
        using var duplicate = new MemoryStream("x=1\nx=2"u8.ToArray());
        Assert.True(PatchFormatProbe.Read(duplicate, PatchFormatKind.Ini, TestContext.Current.CancellationToken).Rejected);
    }

    [Fact]
    public void Read_LargeSparseRawChunk_ReadsOnlyBoundedMetadata()
    {
        const uint rawBytes = 256 * 1024 * 1024;
        var header = Convert.FromHexString("3aff26ed010000001c000c0000100000000001000100000000000000c1ca0000000001000c000010");
        using var stream = new VirtualSparseStream(header, rawBytes + 40L);
        var result = PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Matches(PatchFormatKind.Sparse, PatchFormatEvidence.Structure));
        Assert.InRange(stream.BytesRead, 40, 4096 + 40);
    }

    [Fact]
    public void Read_ExcessiveSparseChunkCount_RejectsBeforeScanning()
    {
        var header = Convert.FromHexString("3aff26ed010000001c000c000010000001000000a186010000000000");
        using var stream = new MemoryStream(header);
        Assert.True(PatchFormatProbe.Read(stream, cancellationToken: TestContext.Current.CancellationToken).Rejected);
    }

    [Fact]
    public void Read_CancelledProbe_ThrowsAndRestoresPosition()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        using var stream = new MemoryStream("{}"u8.ToArray());
        stream.Position = 1;
        Assert.Throws<OperationCanceledException>(() => PatchFormatProbe.Read(stream, cancellationToken: cancellation.Token));
        Assert.Equal(1, stream.Position);
    }

    private sealed class VirtualSparseStream(byte[] header, long length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => true;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - Position);
            var target = buffer[..count];
            target.Clear();
            if (Position < header.Length)
            {
                header.AsSpan((int)Position, (int)Math.Min(count, header.Length - Position)).CopyTo(target);
            }
            Position += count;
            BytesRead += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}