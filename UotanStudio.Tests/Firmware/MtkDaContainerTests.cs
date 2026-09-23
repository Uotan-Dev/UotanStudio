using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public partial class MtkDaContainerTests
{
    [Theory]
    [InlineData(MtkDaDirectoryLayout.Legacy, false)]
    [InlineData(MtkDaDirectoryLayout.Modern, false)]
    [InlineData(MtkDaDirectoryLayout.Modern, true)]
    public void Read_KnownDirectory_PreservesFieldsAndCallerPosition(MtkDaDirectoryLayout layout, bool v6)
    {
        using var source = new MetadataOnlyStream(Container(layout, v6));
        source.Position = 7;

        var container = MtkDaContainerReader.Read(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(layout, container.Layout);
        Assert.Equal(v6, container.HeaderIndicatesV6);
        var entry = Assert.Single(container.Entries);
        Assert.Equal(0x766, entry.HardwareCode);
        Assert.Equal(0x8A00, entry.HardwareSubCode);
        Assert.Equal(0xCA00, entry.HardwareVersion);
        Assert.Equal(layout == MtkDaDirectoryLayout.Legacy ? (ushort?)null : (ushort)2, entry.SoftwareVersion);
        Assert.Equal(4096, entry.PageSize);
        Assert.Equal(1, entry.EntryRegionIndex);
        var region = entry.Regions[1];
        Assert.Equal(1056u, region.FileOffset);
        Assert.Equal(64u, region.Length);
        Assert.Equal(0x40000000u, region.LoadAddress);
        Assert.Equal(48u, region.StartOffset);
        Assert.Equal(16u, region.SignatureLength);
        Assert.Equal(7, source.Position);
        Assert.True(source.CanRead);
        Assert.InRange(source.TotalRead, 108, 548);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("signature")]
    [InlineData("count-zero")]
    [InlineData("count-huge")]
    [InlineData("truncated-table")]
    [InlineData("entry-magic")]
    [InlineData("regions-zero")]
    [InlineData("regions-huge")]
    [InlineData("entry-index")]
    [InlineData("metadata-overlap")]
    [InlineData("region-overrun")]
    [InlineData("signature-overrun")]
    public void Read_InvalidMetadata_RejectsAndRestoresPosition(string corruption)
    {
        var data = Container(MtkDaDirectoryLayout.Modern);
        switch (corruption)
        {
            case "header": data[0] = 0; break;
            case "signature": data[100] = 0; break;
            case "count-zero": Put32(data, 104, 0); break;
            case "count-huge": Put32(data, 104, uint.MaxValue); break;
            case "truncated-table": data = data[..200]; break;
            case "entry-magic": data[108] = 0; break;
            case "regions-zero": Put16(data, 126, 0); break;
            case "regions-huge": Put16(data, 126, 11); break;
            case "entry-index": Put16(data, 124, 2); break;
            case "metadata-overlap": Put32(data, 128, 100); break;
            case "region-overrun": Put32(data, 128, uint.MaxValue); break;
            case "signature-overrun": Put32(data, 144, 33); break;
        }
        using var source = new MemoryStream(data);
        source.Position = 3;

        Assert.Throws<InvalidDataException>(() =>
            MtkDaContainerReader.Read(source, MtkDaDirectoryLayout.Modern, TestContext.Current.CancellationToken));

        Assert.Equal(3, source.Position);
    }

    [Fact]
    public void Read_PreCancelled_LeavesSourceUntouched()
    {
        using var source = new MetadataOnlyStream(Container(MtkDaDirectoryLayout.Modern));
        using var cancellation = new CancellationTokenSource();
        source.Position = 9;
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => MtkDaContainerReader.Read(source, cancellationToken: cancellation.Token));

        Assert.Equal(9, source.Position);
        Assert.Equal(0, source.TotalRead);
    }

    [Fact]
    public void Read_AmbiguousSingleEntry_RequiresExplicitLayout()
    {
        var bytes = Container(MtkDaDirectoryLayout.Modern);
        // Both interpretations are structurally valid but have different region descriptors.
        Put16(bytes, 120, 0);
        Put16(bytes, 122, 1);
        Put16(bytes, 124, 0);
        Put16(bytes, 126, 1);
        Put32(bytes, 128, 1024);
        Put32(bytes, 132, 0);
        Put32(bytes, 136, 0);
        Put32(bytes, 140, 0);
        Put32(bytes, 144, 0);
        Array.Resize(ref bytes, 131072);
        using var source = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => MtkDaContainerReader.Read(source, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(MtkDaDirectoryLayout.Modern, MtkDaContainerReader.Read(source, MtkDaDirectoryLayout.Modern, TestContext.Current.CancellationToken).Layout);
        Assert.Equal(MtkDaDirectoryLayout.Legacy, MtkDaContainerReader.Read(source, MtkDaDirectoryLayout.Legacy, TestContext.Current.CancellationToken).Layout);
    }

    private static byte[] Container(MtkDaDirectoryLayout layout, bool v6 = false)
    {
        var bytes = new byte[1120];
        "MTK_DOWNLOAD_AGENT"u8.CopyTo(bytes);
        if (v6)
        {
            "MTK_DA_v6"u8.CopyTo(bytes.AsSpan(32));
        }
        Put32(bytes, 100, 0x22668899);
        Put32(bytes, 104, 1);
        Put16(bytes, 108, 0xDADA);
        Put16(bytes, 110, 0x766);
        Put16(bytes, 112, 0x8A00);
        Put16(bytes, 114, 0xCA00);
        var headerLength = layout == MtkDaDirectoryLayout.Legacy ? 16 : 20;
        if (layout == MtkDaDirectoryLayout.Modern)
        {
            Put16(bytes, 116, 2);
        }
        Put16(bytes, 108 + headerLength - 8, 4096);
        Put16(bytes, 108 + headerLength - 4, 1);
        Put16(bytes, 108 + headerLength - 2, 2);
        var region = 108 + headerLength;
        Put32(bytes, region, 1024);
        Put32(bytes, region + 4, 32);
        Put32(bytes, region + 8, 0x200000);
        Put32(bytes, region + 20, 1056);
        Put32(bytes, region + 24, 64);
        Put32(bytes, region + 28, 0x40000000);
        Put32(bytes, region + 32, 48);
        Put32(bytes, region + 36, 16);
        return bytes;
    }

    private static void Put16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Put32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    private sealed class MetadataOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int TotalRead { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            var temporary = new byte[buffer.Length];
            var read = Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Assert.True(Position + count <= 548, "Region payload must not be read by metadata parsing.");
            var read = base.Read(buffer, offset, count);
            TotalRead += read;
            return read;
        }
    }
}