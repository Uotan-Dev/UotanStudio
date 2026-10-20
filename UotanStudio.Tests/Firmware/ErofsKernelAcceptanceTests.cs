using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the two ways a repo-written EROFS image could mount yet be unusable by a real kernel, both of
/// which a managed round-trip alone cannot catch. A compressed payload that decodes to fewer bytes than
/// its inode declares still satisfies an in-memory round-trip, and an <c>i_format</c> word carrying a bit
/// outside <c>EROFS_I_ALL</c> still lets the managed parser read an image the kernel refuses with
/// <c>unsupported i_format</c>. The kernel mask spans the nlink-one bit, so <c>0x1F</c> is the widest word
/// both sides accept.
/// <para>守护仓库写出的 EROFS 镜像"能挂载却无法被真实内核使用"的两种情形——托管往返都发现不了：
/// 解压后字节数少于 inode 声明值的压缩负载，仍能满足内存往返；而携带 <c>EROFS_I_ALL</c> 之外位的
/// <c>i_format</c>，仍让托管解析器读到内核以 <c>unsupported i_format</c> 拒绝的镜像。该内核掩码覆盖
/// nlink-one 位，因此 <c>0x1F</c> 是双方都接受的最宽字。</para>
/// </summary>
public sealed class ErofsKernelAcceptanceTests
{
    private const int BlockSize = 4096;

    private const ushort KernelInodeFormatAll = 0x1F;
    private const ushort NlinkOneOrDotOmitted = 1 << 4;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_AnyLayout_LeavesEveryInodeFormatWithinTheKernelMask(bool legacy)
    {
        using var source = new TempDirectory();
        source.WriteBytes("big.bin", LargeCompressiblePayload());
        source.WriteBytes("plain.txt", "plain payload"u8.ToArray());

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = legacy,
        });

        Assert.Empty(InodesRejectedByKernel(image));
    }

    [Fact]
    public void Build_TargetDeclaredToDefineTheAdviseBit_KeepsTheInodeInsideTheKernelMaskAndReadable()
    {
        const ushort declaredFormat = 20;
        Assert.Equal(0, declaredFormat & ~KernelInodeFormatAll);
        Assert.NotEqual(0, declaredFormat & NlinkOneOrDotOmitted);

        using var source = new TempDirectory();
        source.WriteBytes("plain.txt", "plain payload"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            NlinkOneAdviseSupported = true,
        });

        var formats = InodeFormatsInImage(image);
        Assert.Contains(formats, format => (format & NlinkOneOrDotOmitted) != 0);
        Assert.Empty(InodesRejectedByKernel(image));

        using var parser = new ErofsParser(image);
        var entries = parser.EnumerateFiles().ToArray();
        Assert.Contains(entries, entry => entry.Path == "plain.txt");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_CompressedMultiClusterPayload_ReadsBackEveryDeclaredByte(bool legacy)
    {
        var payload = LargeCompressiblePayload();
        using var source = new TempDirectory();
        source.WriteBytes("big.bin", payload);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = legacy,
        });

        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().First(candidate => candidate.Path == "big.bin");
        var read = parser.ReadFileBytes("big.bin");

        Assert.Equal(payload.Length, entry.Size);
        Assert.Equal(payload.Length, read.Length);
        Assert.Equal(payload, read);
    }

    private static byte[] LargeCompressiblePayload()
    {
        var bytes = new byte[4 * 1024 * 1024];
        var tile = new byte[64 * 1024];
        for (var offset = 0; offset < tile.Length; offset++)
        {
            tile[offset] = (byte)((offset * 7) + (offset / 251) * 13);
        }

        var block = 0;
        for (var offset = 0; offset < bytes.Length; offset += tile.Length, block++)
        {
            tile.CopyTo(bytes.AsSpan(offset, tile.Length));
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), block);
        }

        return bytes;
    }

    private static List<string> InodesRejectedByKernel(byte[] image)
    {
        using var parser = new ErofsParser(image);
        var rejected = new List<string>();
        foreach (var entry in parser.EnumerateFiles())
        {
            var offset = parser.Superblock.GetInodeOffset(entry.Nid);
            if (offset < 0 || offset + 2 > image.Length)
            {
                continue;
            }

            var format = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)offset));
            if ((format & ~KernelInodeFormatAll) != 0)
            {
                rejected.Add($"nid {entry.Nid} ('{entry.Path}') i_format {format}");
            }
        }

        return rejected;
    }

    /// <summary>
    /// Reads the <c>i_format</c> word of every inode slot the image actually fills. The raw slot scan is
    /// used instead of the parser only when the image is expected to be unreadable, which is exactly the
    /// condition under test; unused slots past the last inode are skipped so padding cannot masquerade as
    /// a live inode.
    /// <para>读取镜像实际填充的每个 inode 槽的 <c>i_format</c> 字。仅在镜像预期不可读时使用解析器之外的原始
    /// 槽扫描，因为那正是被测条件；最后一个 inode 之后的未用槽会被跳过，使填充字节不会伪装成有效 inode。</para>
    /// </summary>
    private static List<ushort> InodeFormatsInImage(byte[] image)
    {
        var superblock = ErofsSuperblock.FromImage(image);
        var formats = new List<ushort>();
        var limit = (long)image.Length - superblock.MetadataByteOffset;
        for (long nid = 0; nid * ErofsSuperblock.CompactInodeSize + 2 <= limit; nid++)
        {
            var format = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan((int)(superblock.MetadataByteOffset + nid * ErofsSuperblock.CompactInodeSize)));
            if (format == 0)
            {
                continue;
            }

            formats.Add(format);
        }

        return formats;
    }
}