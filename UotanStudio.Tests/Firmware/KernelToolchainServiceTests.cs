using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Kernel;
using UotanStudio.Firmware.Executable.Elf;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class KernelToolchainServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-kernel-toolchain-" + Guid.NewGuid().ToString("N"));

    public KernelToolchainServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadKallsymsTextAsync_ValidLines_ParsesSymbols()
    {
        var path = Path.Combine(_root, "kallsyms.txt");
        await File.WriteAllTextAsync(path, "ffffff8008000000 T _stext\nffffff8008001000 t secondary_startup\n",
            TestContext.Current.CancellationToken);

        var symbols = await KernelToolchainService.ReadKallsymsTextAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(2, symbols.Count);
        Assert.Equal("_stext", symbols[0].Name);
        Assert.Equal('t', symbols[1].Type);
    }

    [Fact]
    public async Task AnalyzeAsync_GzipKernel_ReportsCompressionAndRelease()
    {
        var path = Path.Combine(_root, "kernel.gz");
        await using (var output = new FileStream(path, FileMode.CreateNew))
        await using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
            await gzip.WriteAsync("Linux version 6.1.12-android14-11\0"u8.ToArray(), TestContext.Current.CancellationToken);

        var result = await KernelToolchainService.AnalyzeAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType.Gzip, result.Compression);
        Assert.Equal("6.1.12-android14-11", result.Release);
        Assert.True(result.DecompressedLength > 0);
    }

    [Fact]
    public async Task ReadKallsymsBinaryAsync_CompressedTokenTable_DiscoversAndDecodesSymbols()
    {
        var path = Path.Combine(_root, "kernel.bin");
        var fixture = CreateKallsymsKernel(800, out var relativeBase);
        await File.WriteAllBytesAsync(path, fixture, TestContext.Current.CancellationToken);

        var result = await KernelToolchainService.ReadKallsymsBinaryAsync(path, TestContext.Current.CancellationToken);
        var textPath = Path.Combine(_root, "kallsyms-output.txt");
        var metadata = await KernelToolchainService.ExtractKallsymsAsync(
            path,
            textPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(800, result.Symbols.Count);
        Assert.Equal(relativeBase, result.Metadata.RelativeBase);
        Assert.True(result.Metadata.IsBaseRelative);
        Assert.False(result.Metadata.IsBigEndian);
        Assert.Equal("symbol0000", result.Symbols[0].Name);
        Assert.Equal('T', result.Symbols[0].Type);
        Assert.Equal(relativeBase + 799 * 16UL, result.Symbols[^1].Address);
        Assert.Equal(result.Metadata, metadata);
        var lines = await File.ReadAllLinesAsync(textPath, TestContext.Current.CancellationToken);
        Assert.Equal("ffffff8008000000 T symbol0000", lines[0]);
        Assert.Equal(800, lines.Length);
    }

    [Fact]
    public async Task BuildSymbolizedElfAsync_BinaryKallsyms_WritesLoadSegmentAndSymbolSections()
    {
        var kernelPath = Path.Combine(_root, "symbolized-kernel.bin");
        var fixture = CreateKallsymsKernel(800, out var relativeBase);
        await File.WriteAllBytesAsync(kernelPath, fixture, TestContext.Current.CancellationToken);
        var elfPath = Path.Combine(_root, "kernel.elf");

        var result = await KernelToolchainService.BuildSymbolizedElfAsync(
            kernelPath,
            elfPath,
            new KernelElfOptions { Machine = KernelElfMachine.AArch64, LoadAddress = relativeBase },
            TestContext.Current.CancellationToken);
        var elf = await ElfReader.ReadAsync(elfPath, TestContext.Current.CancellationToken);

        Assert.Equal(KernelElfMachine.AArch64, result.Machine);
        Assert.Equal(800, result.SymbolCount);
        Assert.Equal(relativeBase, result.EntryAddress);
        Assert.Equal(183, elf.Header.Machine);
        Assert.Equal(relativeBase, Assert.Single(elf.Segments).VirtualAddress);
        Assert.Contains(elf.Sections, section => section.Name == ".text" && section.Size == (ulong)fixture.Length);
        Assert.Contains(elf.Sections, section => section.Name == ".symtab" && section.Size == 801UL * 24);
        Assert.Contains(elf.Sections, section => section.Name == ".strtab");
    }

    private static byte[] CreateKallsymsKernel(int symbolCount, out ulong relativeBase)
    {
        relativeBase = 0xFFFFFF8008000000;
        using var output = new MemoryStream();
        output.SetLength(4096);
        output.Position = 0x100;
        output.Write("Linux version 6.1.12-android14-11\0"u8);
        output.Position = 4096;
        Span<byte> offsetBytes = stackalloc byte[4];
        for (var index = 0; index < symbolCount; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(offsetBytes, index * 16);
            output.Write(offsetBytes);
        }
        WriteUInt64(output, relativeBase);
        WriteUInt64(output, checked((ulong)symbolCount));

        var namesStart = checked((int)output.Position);
        var markerValues = new List<ulong>();
        for (var index = 0; index < symbolCount; index++)
        {
            if (index % 256 == 0)
            {
                markerValues.Add(checked((ulong)(output.Position - namesStart)));
            }
            var name = Encoding.ASCII.GetBytes($"Tsymbol{index:D4}");
            output.WriteByte(checked((byte)name.Length));
            output.Write(name);
        }
        Align(output, 8);
        foreach (var marker in markerValues)
        {
            WriteUInt64(output, marker);
        }
        Align(output, 4);

        var tokenTableStart = checked((int)output.Position);
        var tokenOffsets = new ushort[256];
        for (var index = 0; index < tokenOffsets.Length; index++)
        {
            tokenOffsets[index] = checked((ushort)(output.Position - tokenTableStart));
            if (index is >= 32 and <= 126)
            {
                output.WriteByte(checked((byte)index));
            }
            output.WriteByte(0);
        }
        Align(output, 2);
        Span<byte> tokenIndexBytes = stackalloc byte[2];
        foreach (var tokenOffset in tokenOffsets)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tokenIndexBytes, tokenOffset);
            output.Write(tokenIndexBytes);
        }
        return output.ToArray();
    }

    private static void WriteUInt64(Stream output, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void Align(Stream output, int alignment)
    {
        while (output.Position % alignment != 0)
        {
            output.WriteByte(0);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}