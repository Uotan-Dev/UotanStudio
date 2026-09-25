using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class MtkEmiReaderTests
{
    [Theory]
    [InlineData(MtkEmiPayloadLayout.BootloaderInfo, 0)]
    [InlineData(MtkEmiPayloadLayout.BinaryParameters, 0)]
    [InlineData(MtkEmiPayloadLayout.BootloaderInfo, 2048)]
    [InlineData(MtkEmiPayloadLayout.BinaryParameters, 2048)]
    public async Task ReadPreloader_ExplicitLayoutAndTrailer_ExcludesSignatureAndPreservesBytes(MtkEmiPayloadLayout layout, int padding)
    {
        using var source = new MemoryStream(Preloader(padding));
        source.Position = 7;

        var parameters = MtkEmiReader.ReadPreloader(source, 512, layout, TestContext.Current.CancellationToken);

        Assert.Equal(12, parameters.Version);
        Assert.Equal(7, source.Position);
        Assert.Equal(padding, parameters.TrailerPaddingBytes);
        var expected = layout == MtkEmiPayloadLayout.BootloaderInfo ? Block() : Block()[44..];
        Assert.Equal(568 + (layout == MtkEmiPayloadLayout.BootloaderInfo ? 0 : 44), parameters.SourceOffset);
        using var payload = parameters.OpenRead();
        using var result = new MemoryStream();
        await payload.CopyToAsync(result, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.ToArray());
        Assert.False(payload.CanWrite);
        Assert.True(source.CanRead);
    }

    [Fact]
    public void ReadBlock_ExplicitOffset_ReturnsRetainedSnapshotAndIndependentViews()
    {
        var bytes = new byte[13 + Block().Length];
        Block().CopyTo(bytes, 13);
        using var source = new MemoryStream(bytes);
        source.Position = 5;

        var parameters = MtkEmiReader.ReadBlock(source, 13, Block().Length, MtkEmiPayloadLayout.BinaryParameters,
            TestContext.Current.CancellationToken);
        Array.Clear(bytes);
        using var first = parameters.OpenRead();
        using var second = parameters.OpenRead();

        Assert.Equal(5, source.Position);
        Assert.Equal(57, parameters.SourceOffset);
        Assert.Equal(1, first.ReadByte());
        Assert.Equal(2, first.ReadByte());
        Assert.Equal(1, second.ReadByte());
    }

    [Theory]
    [InlineData("header")]
    [InlineData("image-length")]
    [InlineData("signature-length")]
    [InlineData("empty")]
    [InlineData("huge")]
    [InlineData("overlap")]
    [InlineData("no-info")]
    [InlineData("version")]
    [InlineData("no-bin")]
    [InlineData("duplicate-bin")]
    public void ReadPreloader_InvalidMetadata_RejectsAndRestoresPosition(string corruption)
    {
        var bytes = Preloader(0);
        var tail = 568 + Block().Length;
        switch (corruption)
        {
            case "header": bytes[512] = 0; break;
            case "image-length": Put32(bytes, 544, uint.MaxValue); break;
            case "signature-length": Put32(bytes, 556, uint.MaxValue); break;
            case "empty": Put32(bytes, tail, 0); break;
            case "huge": Put32(bytes, tail, 1024 * 1024 + 1); break;
            case "overlap": Put32(bytes, tail, (uint)Block().Length + 1); break;
            case "no-info": bytes[568] = 0; break;
            case "version": bytes[568 + 17] = (byte)'X'; break;
            case "no-bin": bytes[568 + 32] = 0; break;
            case "duplicate-bin": "MTK_BIN"u8.CopyTo(bytes.AsSpan(568 + 60)); break;
        }
        using var source = new MemoryStream(bytes);
        source.Position = 3;

        Assert.Throws<InvalidDataException>(() => MtkEmiReader.ReadPreloader(source, 512,
            MtkEmiPayloadLayout.BinaryParameters, TestContext.Current.CancellationToken));

        Assert.Equal(3, source.Position);
    }

    [Theory]
    [InlineData(-1L, 92)]
    [InlineData(long.MaxValue, 92)]
    [InlineData(0L, 0)]
    [InlineData(0L, 1048577)]
    public void ReadBlock_InvalidRange_RejectsBeforeAllocation(long offset, int length)
    {
        using var source = new MemoryStream(Block());

        Assert.Throws<InvalidDataException>(() => MtkEmiReader.ReadBlock(source, offset, length,
            MtkEmiPayloadLayout.BootloaderInfo, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadPreloader_PreCancelled_PreservesSourcePosition()
    {
        using var source = new MemoryStream(Preloader(0));
        using var cancellation = new CancellationTokenSource();
        source.Position = 8;
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => MtkEmiReader.ReadPreloader(source, 512,
            MtkEmiPayloadLayout.BootloaderInfo, cancellation.Token));

        Assert.Equal(8, source.Position);
    }

    private static byte[] Block()
    {
        var bytes = new byte[92];
        "MTK_BLOADER_INFO_v12"u8.CopyTo(bytes);
        "MTK_BIN"u8.CopyTo(bytes.AsSpan(32));
        for (var index = 44; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index - 43);
        }
        return bytes;
    }

    private static byte[] Preloader(int padding)
    {
        var block = Block();
        var imageLength = 56 + block.Length + 4 + padding + 64;
        var bytes = new byte[512 + imageLength];
        new byte[] { 0x4D, 0x4D, 0x4D, 1, 0x38, 0, 0, 0 }.CopyTo(bytes, 512);
        Put32(bytes, 544, (uint)imageLength);
        Put32(bytes, 556, 64);
        block.CopyTo(bytes, 568);
        Put32(bytes, 568 + block.Length, (uint)block.Length);
        bytes.AsSpan(bytes.Length - 64).Fill(0xCC);
        return bytes;
    }

    private static void Put32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}