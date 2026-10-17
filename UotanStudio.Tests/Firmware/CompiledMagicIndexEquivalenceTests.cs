using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Magic;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class CompiledMagicIndexEquivalenceTests
{
    [Fact]
    public void Match_IndexedEngine_AgreesWithBruteForceOnConstructedCorpus()
    {
        CompiledMagicEngine.EnsureLoaded();
        Assert.True(CompiledMagicEngine.IndexedRuleCount > 0);
        Assert.True(CompiledMagicEngine.FallbackRuleCount > 0);
        Assert.True(CompiledMagicEngine.IndexedOffsetCount > 0);
        Assert.True(CompiledMagicEngine.IndexedRuleCount < CompiledMagicEngine.TopLevelCount);

        foreach (var (name, data) in BuildCorpus())
        {
            AssertSameResult(name, data);
        }
    }

    [Fact]
    public void Match_IndexedEngine_AgreesWithBruteForceOnRandomBytes()
    {
        var random = new Random(20261018);
        for (var iteration = 0; iteration < 1024; iteration++)
        {
            var length = iteration % 16 == 0 ? 1 + random.Next(8192) : 1 + random.Next(256);
            var data = new byte[length];
            random.NextBytes(data);
            if (iteration % 4 == 0)
            {
                var seed = KnownMagicSeeds[random.Next(KnownMagicSeeds.Length)];
                seed.AsSpan(0, Math.Min(seed.Length, data.Length)).CopyTo(data);
            }

            AssertSameResult($"random-{iteration}", data);
        }
    }

    [Fact]
    public void Match_IndexedEngine_AgreesWithBruteForceForSingleByteValues()
    {
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            AssertSameResult($"single-{value}", [0x00, (byte)value]);
            AssertSameResult($"single-leading-{value}", [(byte)value, 0x00]);
        }
    }

    [Fact]
    public void Match_IndexedEngine_AgreesWithBruteForceAcrossZeroFilledOffsets()
    {
        var data = new byte[4200];
        for (var offset = 0; offset < 2048; offset += 7)
        {
            data[offset] = 0x55;
            AssertSameResult($"offset-{offset}", data);
            data[offset] = 0x00;
        }
    }

    private static void AssertSameResult(string name, byte[] data)
    {
        var indexed = CompiledMagicEngine.Match(data);
        var brute = CompiledMagicEngine.MatchWithoutIndex(data);
        Assert.True(Describe(indexed) == Describe(brute),
            $"{name}: indexed={Describe(indexed)} brute={Describe(brute)}");
    }

    private static string Describe(MagicInfo? info)
        => info is null
            ? "<null>"
            : $"{info.Strength}|{info.Description}|{info.Source}|{info.MimeType}|{info.Extensions}|{info.Encoding}|{info.IsText}";

    private static readonly byte[][] KnownMagicSeeds =
    [
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        [0x1F, 0x8B, 0x08, 0x00],
        [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00],
        [0x28, 0xB5, 0x2F, 0xFD],
        [0x42, 0x5A, 0x68],
        [0x50, 0x4B, 0x03, 0x04],
        [0x7F, 0x45, 0x4C, 0x46],
        [0x25, 0x50, 0x44, 0x46],
        [0xFF, 0xD8, 0xFF, 0xE0],
        [0x47, 0x49, 0x46, 0x38],
        [0x52, 0x49, 0x46, 0x46],
        [0x4F, 0x67, 0x67, 0x53],
        [0x66, 0x4C, 0x61, 0x43],
        [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65],
        [0xD0, 0x0D, 0xFE, 0xED],
        [0x3A, 0xFF, 0x26, 0xED],
        [0x30, 0x37, 0x30, 0x37, 0x30, 0x31],
    ];

    private static IEnumerable<(string Name, byte[] Data)> BuildCorpus()
    {
        yield return ("empty", []);
        yield return ("nul", new byte[64]);
        yield return ("text", Encoding.ASCII.GetBytes("hello world\nsecond line\n"));
        yield return ("utf8", Encoding.UTF8.GetBytes("固件格式识别"));
        yield return ("png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52]);
        yield return ("zip", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00]);
        yield return ("gzip", [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03]);
        yield return ("elf", BuildElf());

        var ext4 = new byte[2048];
        ext4[1024 + 56] = 0x53;
        ext4[1024 + 57] = 0xEF;
        yield return ("ext4", ext4);

        var romfs = new byte[256];
        "-rom1fs-"u8.CopyTo(romfs);
        BinaryPrimitives.WriteUInt32BigEndian(romfs.AsSpan(8), 64);
        yield return ("romfs", romfs);

        var squashfs = new byte[512];
        "hsqs"u8.CopyTo(squashfs);
        BinaryPrimitives.WriteUInt32LittleEndian(squashfs.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(squashfs.AsSpan(12), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(squashfs.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(squashfs.AsSpan(22), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(squashfs.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(squashfs.AsSpan(28), 4);
        yield return ("squashfs", squashfs);

        var fdt = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(fdt, 0xD00DFEED);
        yield return ("fdt", fdt);

        yield return ("sparse", [0x3A, 0xFF, 0x26, 0xED, 0x10, 0x00, 0x00, 0x00]);
        yield return ("cpio", [0x30, 0x37, 0x30, 0x37, 0x30, 0x31, 0x30, 0x30]);
        yield return ("lz4", [0x04, 0x22, 0x4D, 0x18, 0x64, 0x70, 0x62, 0x64]);
        yield return ("xz", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x01, 0x04]);
        yield return ("zstd", [0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x05, 0x40, 0x02]);
        yield return ("bzip2", [0x42, 0x5A, 0x68, 0x39, 0x31, 0x41, 0x59, 0x26]);
        yield return ("pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n"));
        yield return ("jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        yield return ("gif", [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00]);
        yield return ("bmp", [0x42, 0x4D, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00]);
        yield return ("tiff", [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        yield return ("sqlite", Encoding.ASCII.GetBytes("SQLite format 3\0"));
        yield return ("wasm", [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00]);
        yield return ("ole2", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        yield return ("pcap", [0xD4, 0xC3, 0xB2, 0xA1, 0x02, 0x00, 0x04, 0x00]);
        yield return ("java-class", [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34]);

        var big = new byte[1024 * 1024];
        new Random(20261018).NextBytes(big);
        yield return ("random-1m", big);

        var zerosWithText = new byte[8192];
        Encoding.ASCII.GetBytes("androidboot.hardware=qcom").CopyTo(zerosWithText, 4096);
        yield return ("zeros-with-text", zerosWithText);
    }

    private static byte[] BuildElf()
    {
        var elf = new byte[256];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        elf[16] = 3;
        elf[17] = 2;
        elf[18] = 0xB7;
        elf[20] = 1;
        return elf;
    }
}