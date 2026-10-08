using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Gates the F2FS inode xattr layout on raw image bytes and a native reference rather than on this
/// repository's own parser, because the parser mirrors the writer and can never observe an inline
/// xattr anchored at the wrong address word.
/// <para>按原始镜像字节与原生参照校验 F2FS 的 inode xattr 布局，不依赖本仓库自身的解析器；
/// 解析器与写入器互为镜像，永远观察不到内联 xattr 锚定在错误的地址字上。</para>
/// </summary>
/// <remarks>
/// Provenance of the native constants: captured on this machine from f2fs images written by the
/// running kernel (Linux 6.18.40.1-microsoft-standard-WSL2, with <c>mkfs.f2fs</c>,
/// <c>dump.f2fs</c>, <c>setfacl</c> and <c>f2fs-tools 1.16.0</c>) by formatting with
/// <c>mkfs.f2fs -O extra_attr,flexible_inline_xattr,inode_crtime,project_quota</c>, mounting,
/// creating a tree, unmounting and reading node pages by their footer (<c>nid == ino</c> marks an
/// inode node). The Alpine <c>sload.f2fs</c> prints <c>Not support selinux opts</c> and this kernel
/// has <c>CONFIG_F2FS_FS_SECURITY</c> disabled, so kernel-written <c>system.posix_acl_access</c>
/// attributes stand in for <c>security.selinux</c>: both travel through the same
/// <c>f2fs_xattr_header</c>, the same <c>f2fs_xattr_entry</c> encoding and the same
/// <c>i_addr[923 - i_inline_xattr_size]</c> placement.
/// The observed native values are, for an inode that owns an inline xattr region,
/// <c>i_extra_isize = 36</c> (<c>F2FS_TOTAL_EXTRA_ATTR_SIZE</c>, which the 5.10 Android common kernel
/// also writes unconditionally once the extra-attribute feature is on),
/// <c>i_inline = 0x2b</c> and the header word at <c>i_addr[873]</c> holding magic <c>0xF2F52011</c>,
/// reference count <c>1</c> and sixteen zero bytes — there is no entry count, total size, block
/// address or version field in this header; the first entry follows immediately with
/// <c>e_name_index = 2, e_name_len = 0, e_value_size = 36</c>. For a root directory the same kernel
/// keeps the dentry block address at <c>i_addr[i_extra_isize / 4]</c> (native
/// <c>i_extra_isize = 24</c> therefore put it in <c>i_addr[6] = 5635</c>) and moved the attribute into
/// an external xattr node whose footer flag is <c>0xFFFFFFF8</c> and whose payload starts at block
/// offset 0 with the same 24-byte header. Inline data uses <c>i_addr[i_extra_isize / 4 + 1]</c>, the
/// native inline-dentry inode showing its dentry word at <c>i_addr[10]</c> under
/// <c>i_extra_isize = 36</c>. The full 4096-byte node pages live outside the repository at
/// <c>E:\GkiFsLab\corpus\native-f2fs\</c>; the base64 fields below are byte copies of the windows
/// these tests compare, and each assertion names the ranges it masks (the three timestamps with their
/// nanoseconds, the birth time with its nanoseconds, and the block numbers). Kernel acceptance on a
/// device is not proven by this suite.
/// <para>原生常量的来源：在本机由运行中的内核写入的 f2fs 镜像采集（Linux 6.18.40.1-microsoft-standard-WSL2，
/// 工具为 <c>mkfs.f2fs</c>、<c>dump.f2fs</c>、<c>setfacl</c> 与 <c>f2fs-tools 1.16.0</c>）：先用
/// <c>mkfs.f2fs -O extra_attr,flexible_inline_xattr,inode_crtime,project_quota</c> 格式化，再挂载、生成目录树、
/// 卸载，然后按页脚读取节点页（<c>nid == ino</c> 即 inode 节点）。本机的 Alpine 版 <c>sload.f2fs</c> 会打印
/// <c>Not support selinux opts</c>，且该内核关闭了 <c>CONFIG_F2FS_FS_SECURITY</c>，因此改由内核写入的
/// <c>system.posix_acl_access</c> 属性代替 <c>security.selinux</c>：两者共用同一个
/// <c>f2fs_xattr_header</c>、同一套 <c>f2fs_xattr_entry</c> 编码，以及同一个
/// <c>i_addr[923 - i_inline_xattr_size]</c> 位置。
/// 观察到的原生值是：拥有内联 xattr 区的 inode 的 <c>i_extra_isize = 36</c>
/// （即 <c>F2FS_TOTAL_EXTRA_ATTR_SIZE</c>，5.10 Android 通用内核在开启扩展属性特性后也无条件写入该值），
/// <c>i_inline = 0x2b</c>，头部字位于 <c>i_addr[873]</c>，内容是幻数 <c>0xF2F52011</c>、引用计数 <c>1</c>
/// 与 16 个零字节——该头部没有条目计数、总大小、块地址或版本字段；首个条目紧随其后，为
/// <c>e_name_index = 2, e_name_len = 0, e_value_size = 36</c>。根目录的 dentry 块地址仍留在
/// <c>i_addr[i_extra_isize / 4]</c>（原生 <c>i_extra_isize = 24</c>，故落在 <c>i_addr[6] = 5635</c>），
/// 而属性被移到外部 xattr 节点，其页脚 flag 为 <c>0xFFFFFFF8</c>，载荷从块偏移 0 开始并以同一个 24 字节头
/// 起始。内联数据使用 <c>i_addr[i_extra_isize / 4 + 1]</c>：原生内联 dentry inode 在
/// <c>i_extra_isize = 36</c> 下把 dentry 字放在 <c>i_addr[10]</c>。完整的 4096 字节节点页保存在仓库之外的
/// <c>E:\GkiFsLab\corpus\native-f2fs\</c>，下面的 base64 字段就是本测试所比较窗口的字节副本；每条断言都点名列出
/// 屏蔽范围（三个时间戳及其纳秒、创建时间及其纳秒、块号）。本测试集不能证明设备可挂载。</para>
/// </remarks>
public class F2fsInodeXattrLayoutTests
{
    private const int NodeSize = 4096;
    private const int AddrArrayOffset = 360;
    private const int DefAddrsPerInode = 923;
    private const int FooterOffset = NodeSize - 24;
    private const int ExtraIsizeFieldOffset = 360;
    private const int InlineXattrSizeFieldOffset = 362;
    private const int InlineFlagsOffset = 3;
    private const int NameLengthFieldOffset = 88;
    private const int NameFieldOffset = 92;
    private const int XattrNidFieldOffset = 76;
    private const int SuperblockFeatureOffset = 1024 + 2180;
    private const uint FeatureExtraAttr = 0x08;
    private const uint FeatureFlexibleInlineXattr = 0x40;
    private const byte InlineXattrFlag = 0x01;
    private const byte ExtraAttrFlag = 0x20;
    private const int NativeExtraIsizeBytes = 36;
    private const int NativeRootExtraIsizeBytes = 24;
    private const int XattrHeaderBytes = 24;
    private const uint XattrMagic = 0xF2F52011;
    private const uint XattrNodeFooterFlag = 0xFFFF_FFF8;
    private const int DirentEntryAreaOffset = 30;
    private const int DirentNameAreaOffset = 2384;
    private const int DirentEntrySize = 11;
    private const int RootIno = 3;
    private const int SecurityXattrIndex = 6;
    private const int NativePosixAclAccessIndex = 2;
    private const ushort NativePosixAclValueSize = 36;
    private const uint NativeRootDentryBlock = 5635;
    private const byte NativeRootDentryBitmap = 0x1F;
    private const int NativeInlineXattrReservationWords = 50;
    private const int NativeInlineXattrWordNumber = DefAddrsPerInode - NativeInlineXattrReservationWords;
    private const int SelinuxLabelBytes = 25;
    private const string SelinuxLabel = "u:object_r:system_file:s0";
    private const string SelinuxName = "selinux";
    private const int SelinuxNameBytes = 7;
    private const int SelinuxValueBytes = SelinuxLabelBytes + 1;
    private const int SelinuxEntryBytes = 4 + SelinuxNameBytes + SelinuxValueBytes;
    private const int InlineXattrTotalBytes = XattrHeaderBytes + ((SelinuxEntryBytes + 3) & ~3) + 4;
    private const int InlineXattrWords = InlineXattrTotalBytes / 4;
    private const int OverflowValuePaddingBytes = 3600;
    private const int FirstUsableAddrByte = AddrArrayOffset + NativeExtraIsizeBytes;
    private const int NativeCrtimeFieldOffset = AddrArrayOffset + 12;
    private const int NativeFirstAddrWordByte = AddrArrayOffset + NativeRootExtraIsizeBytes;

    private static readonly byte[] NativeRootHeaderWindow = Convert.FromBase64String(
        "7UEAIAAAAAAAAAAAAwAAAAAQAAAAAAAAAgAAAAAAAAAWmcZqAAAAABaZxmoAAAAAFpnGagAAAAAAAAAA3pKnA96SpwMAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAAAAAAAAAAAAAAAFpnGagAAAAAAAAAAAxYAAAAAAAAAAAAAAAAAAAAAAAA=");

    private static readonly byte[] NativeInlineXattrHeaderWindow = Convert.FromBase64String(
        "ESD18gEAAAAAAAAAAAAAAAAAAAAAAAAAAgAkAAEAAAABAAcAAgAHAAAAAAAEAAcACAAHALsLAAAQAAcAIAAAAA==");

    private static readonly byte[] NativeDirentBlockWindow = Convert.FromBase64String(
        "HwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMAAAABAAIAAAAAAwAAAAIAAg==");

    [Fact]
    public void Build_WithFileContexts_RootDentryBlockAddressSitsAtExtraAttributeBase()
    {
        using var source = new ScratchDir();
        using var meta = new ScratchDir();
        WriteTree(source);
        var image = F2fsBuilder.Build(source.Path, LabeledOptions(meta));
        var root = ReadInodeNodeByNid(image, RootIno);
        var extraIsize = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(ExtraIsizeFieldOffset, 2));

        Assert.Equal(NativeExtraIsizeBytes, (int)extraIsize);
        Assert.Equal(FirstUsableAddrByte, AddrArrayOffset + extraIsize / 4 * 4);
        Assert.True((root[InlineFlagsOffset] & ExtraAttrFlag) != 0);
        Assert.True((root[InlineFlagsOffset] & InlineXattrFlag) != 0);

        var dentryBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(FirstUsableAddrByte, 4));
        Assert.True(dentryBlock > 0);
        AssertDirentEntries(image, dentryBlock, checkNames: true);
    }

    [Fact]
    public void Build_WithAndWithoutFileContexts_RootAddressWordsStayIdentical()
    {
        using var source = new ScratchDir();
        using var meta = new ScratchDir();
        WriteTree(source);
        var plain = ReadInodeNodeByNid(F2fsBuilder.Build(source.Path), RootIno);
        var labeled = ReadInodeNodeByNid(F2fsBuilder.Build(source.Path, LabeledOptions(meta)), RootIno);

        Assert.Equal(
            BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(ExtraIsizeFieldOffset, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(labeled.AsSpan(ExtraIsizeFieldOffset, 2)));
        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(FirstUsableAddrByte, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(labeled.AsSpan(FirstUsableAddrByte, 4)));
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(labeled.AsSpan(FirstUsableAddrByte, 4)));
        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(AddrArrayOffset + 40, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(labeled.AsSpan(AddrArrayOffset + 40, 4)));

        for (var offset = 0; offset < NodeSize; offset++)
        {
            if (IsAllowedBuildDifference(offset))
            {
                continue;
            }

            Assert.Equal(plain[offset], labeled[offset]);
        }
    }

    [Fact]
    public void Build_WithFileContexts_InlineXattrSizeMatchesEncodedLength()
    {
        using var source = new ScratchDir();
        using var meta = new ScratchDir();
        WriteTree(source);
        var root = ReadInodeNodeByNid(F2fsBuilder.Build(source.Path, LabeledOptions(meta)), RootIno);
        var words = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(InlineXattrSizeFieldOffset, 2));
        var region = InlineXattrRegionOffset(words);

        Assert.Equal(InlineXattrWords, (int)words);
        Assert.True(words < 128);
        Assert.Equal(XattrMagic, BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(region, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(region + 4, 4)));
        AssertZeros(root, region + 8, XattrHeaderBytes - 8);

        var entry = region + XattrHeaderBytes;
        Assert.Equal(SecurityXattrIndex, (int)root[entry]);
        Assert.Equal(SelinuxNameBytes, (int)root[entry + 1]);
        Assert.Equal(SelinuxValueBytes, (int)BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(entry + 2, 2)));
        Assert.Equal(Encoding.UTF8.GetBytes(SelinuxName), root[(entry + 4)..(entry + 4 + SelinuxNameBytes)]);
        Assert.Equal(
            Encoding.UTF8.GetBytes(SelinuxLabel).Append((byte)0).ToArray(),
            root[(entry + 4 + SelinuxNameBytes)..(entry + 4 + SelinuxNameBytes + SelinuxValueBytes)]);
        AssertZeros(root, region + InlineXattrTotalBytes, words * 4 - InlineXattrTotalBytes);
    }

    [Fact]
    public void Build_WithFileContexts_InlineXattrHeaderMatchesNativeFieldOffsets()
    {
        using var source = new ScratchDir();
        using var meta = new ScratchDir();
        WriteTree(source);
        var root = ReadInodeNodeByNid(F2fsBuilder.Build(source.Path, LabeledOptions(meta)), RootIno);
        var region = InlineXattrRegionOffset(
            BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(InlineXattrSizeFieldOffset, 2)));

        Assert.Equal(
            NativeInlineXattrHeaderWindow[..XattrHeaderBytes],
            root[region..(region + XattrHeaderBytes)]);
    }

    [Fact]
    public void Build_WithFileContexts_NoAddressWordOverlapsInlineXattrRegion()
    {
        using var source = new ScratchDir();
        using var meta = new ScratchDir();
        WriteTree(source);
        var root = ReadInodeNodeByNid(F2fsBuilder.Build(source.Path, LabeledOptions(meta)), RootIno);
        var words = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(InlineXattrSizeFieldOffset, 2));
        var regionWord = DefAddrsPerInode - words;
        var dentryBlock = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(FirstUsableAddrByte, 4));

        Assert.True(AddrArrayOffset + regionWord * 4 > FirstUsableAddrByte);

        for (var word = NativeExtraIsizeBytes / 4; word < regionWord; word++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(AddrArrayOffset + word * 4, 4));
            Assert.Equal(word == NativeExtraIsizeBytes / 4 ? dentryBlock : 0u, value);
        }

        Assert.Equal(XattrMagic, BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(AddrArrayOffset + regionWord * 4, 4)));
    }

    [Fact]
    public void Build_WithoutFileContexts_RootInodeMatchesNativeHeaderWindow()
    {
        using var source = new ScratchDir();
        WriteTree(source);
        var root = ReadInodeNodeByNid(F2fsBuilder.Build(source.Path), RootIno);
        var maskedRanges = new (int Start, int End)[]
        {
            (32, 68),
            (ExtraIsizeFieldOffset, ExtraIsizeFieldOffset + 4),
            (NativeCrtimeFieldOffset, NativeCrtimeFieldOffset + 8),
            (NativeFirstAddrWordByte, AddrArrayOffset + 44),
        };

        Assert.Equal(RootIno, (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(FooterOffset, 4)));
        Assert.Equal(RootIno, (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(FooterOffset + 4, 4)));
        Assert.True((root[InlineFlagsOffset] & ExtraAttrFlag) != 0);
        Assert.Equal(0, root[InlineFlagsOffset] & InlineXattrFlag);
        Assert.Equal(NativeExtraIsizeBytes, (int)BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(ExtraIsizeFieldOffset, 2)));
        Assert.Equal(0, (int)BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(InlineXattrSizeFieldOffset, 2)));
        Assert.Equal(0, (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(XattrNidFieldOffset, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(12, 4)));
        Assert.Equal(4096UL, BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(16, 8)));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(24, 8)));

        for (var offset = 0; offset < NativeRootHeaderWindow.Length; offset++)
        {
            if (maskedRanges.Any(range => offset >= range.Start && offset < range.End))
            {
                continue;
            }

            Assert.Equal(NativeRootHeaderWindow[offset], root[offset]);
        }
    }

    [Fact]
    public void Build_WithFileContexts_EveryInodeCarriesExtraAttributeFlag()
    {
        using var source = new ScratchDir();
        using var meta = new ScratchDir();
        WriteTree(source);
        var image = F2fsBuilder.Build(source.Path, LabeledOptions(meta));
        var feature = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SuperblockFeatureOffset, 4));
        var inodeNodes = 0;

        Assert.True((feature & FeatureFlexibleInlineXattr) != 0);
        Assert.True((feature & FeatureExtraAttr) != 0);

        for (var block = 0; block + NodeSize <= image.Length; block += NodeSize)
        {
            var nid = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset, 4));
            var ino = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset + 4, 4));
            if (nid == 0 || nid != ino)
            {
                continue;
            }

            inodeNodes++;
            Assert.True((image[block + InlineFlagsOffset] & ExtraAttrFlag) != 0);
        }

        Assert.True(inodeNodes >= 4);
    }

    [Fact]
    public void Build_WithOversizedXattrs_RemainderSpillsToExternalNodeAfterInlineRegion()
    {
        using var source = new ScratchDir();
        WriteTree(source);
        var value = Encoding.UTF8.GetBytes(SelinuxLabel)
            .Append((byte)0)
            .Concat(new byte[OverflowValuePaddingBytes])
            .ToArray();
        var expected = ExpectedXattrBlock(value);
        var options = new F2fsOptions
        {
            FileXattrs = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal)
            {
                ["labeled.txt"] = new(StringComparer.Ordinal) { ["security.selinux"] = value },
            },
        };
        var image = F2fsBuilder.Build(source.Path, options);
        var fileNode = ReadInodeNodeByName(image, "labeled.txt");
        var ownerIno = BinaryPrimitives.ReadUInt32LittleEndian(fileNode.AsSpan(FooterOffset, 4));
        var words = BinaryPrimitives.ReadUInt16LittleEndian(fileNode.AsSpan(InlineXattrSizeFieldOffset, 2));
        var inlineBytes = words * 4;
        var xattrNid = BinaryPrimitives.ReadUInt32LittleEndian(fileNode.AsSpan(XattrNidFieldOffset, 4));

        Assert.Equal(F2fsInode.MaxInlineXattrWords, (int)words);
        Assert.True(expected.Length > inlineBytes);
        Assert.NotEqual(0u, xattrNid);

        var external = ReadXattrNodeByNid(image, xattrNid);
        var inlineRegion = fileNode[(AddrArrayOffset + DefAddrsPerInode * 4 - inlineBytes)..(AddrArrayOffset + DefAddrsPerInode * 4)];
        Assert.Equal(expected[..inlineBytes], inlineRegion);

        var spillBytes = expected.Length - inlineBytes;
        Assert.Equal(expected[inlineBytes..], external[..spillBytes]);
        AssertZeros(external, spillBytes, NodeSize - F2fsNodeFooter.FooterSize - spillBytes);
        Assert.Equal(xattrNid, BinaryPrimitives.ReadUInt32LittleEndian(external.AsSpan(FooterOffset, 4)));
        Assert.Equal(ownerIno, BinaryPrimitives.ReadUInt32LittleEndian(external.AsSpan(FooterOffset + 4, 4)));
        Assert.Equal(XattrNodeFooterFlag, BinaryPrimitives.ReadUInt32LittleEndian(external.AsSpan(FooterOffset + 8, 4)));
    }

    [Fact]
    public void NativeReference_RootDentryBlockAddressSitsAtExtraAttributeBase()
    {
        var nativeExtraIsize = BinaryPrimitives.ReadUInt16LittleEndian(NativeRootHeaderWindow.AsSpan(ExtraIsizeFieldOffset, 2));

        Assert.Equal(NativeRootExtraIsizeBytes, (int)nativeExtraIsize);
        Assert.Equal(NativeRootDentryBlock, BinaryPrimitives.ReadUInt32LittleEndian(
            NativeRootHeaderWindow.AsSpan(NativeFirstAddrWordByte, 4)));
        Assert.Equal(NativeRootDentryBitmap, NativeDirentBlockWindow[0]);
        Assert.Equal(
            NativeRootExtraIsizeBytes,
            (int)BinaryPrimitives.ReadUInt32LittleEndian(NativeRootHeaderWindow.AsSpan(AddrArrayOffset, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(
            NativeRootHeaderWindow.AsSpan(AddrArrayOffset + (NativeRootExtraIsizeBytes / 4 * 4) + 12, 4)));
        AssertDirentEntries(NativeDirentBlockWindow, 0, checkNames: false);
    }

    [Fact]
    public void NativeReference_InlineXattrRegionAnchorMatchesReservation()
    {
        Assert.Equal(NativeInlineXattrReservationWords, DefAddrsPerInode - NativeInlineXattrWordNumber);
        Assert.Equal(
            AddrArrayOffset + NativeInlineXattrWordNumber * 4,
            InlineXattrRegionOffset((ushort)NativeInlineXattrReservationWords));
    }

    [Fact]
    public void NativeReference_XattrHeaderCarriesNoEntryCountSizeBlockOrVersionField()
    {
        Assert.Equal(XattrMagic, BinaryPrimitives.ReadUInt32LittleEndian(NativeInlineXattrHeaderWindow.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(NativeInlineXattrHeaderWindow.AsSpan(4, 4)));
        AssertZeros(NativeInlineXattrHeaderWindow, 8, XattrHeaderBytes - 8);
        Assert.Equal(NativePosixAclAccessIndex, (int)NativeInlineXattrHeaderWindow[XattrHeaderBytes]);
        Assert.Equal(0, (int)NativeInlineXattrHeaderWindow[XattrHeaderBytes + 1]);
        Assert.Equal(NativePosixAclValueSize, BinaryPrimitives.ReadUInt16LittleEndian(
            NativeInlineXattrHeaderWindow.AsSpan(XattrHeaderBytes + 2, 2)));
    }

    private static bool IsAllowedBuildDifference(int offset)
    {
        var payloadStart = AddrArrayOffset + (DefAddrsPerInode - InlineXattrWords) * 4;
        return offset == InlineFlagsOffset
            || (offset >= 32 && offset < 68)
            || (offset >= InlineXattrSizeFieldOffset && offset < InlineXattrSizeFieldOffset + 2)
            || (offset >= payloadStart && offset < AddrArrayOffset + DefAddrsPerInode * 4);
    }

    private static int InlineXattrRegionOffset(ushort words)
        => AddrArrayOffset + (DefAddrsPerInode - words) * 4;

    private static void WriteTree(ScratchDir source)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(source.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        Directory.CreateDirectory(Path.Combine(source.Path, "dir"));
        File.WriteAllText(Path.Combine(source.Path, "labeled.txt"), "file-bytes\n");
        File.WriteAllText(Path.Combine(source.Path, "dir", "inner.txt"), "inner\n");
    }

    private static F2fsOptions LabeledOptions(ScratchDir meta)
    {
        var path = Path.Combine(meta.Path, "file_contexts");
        File.WriteAllText(path, string.Join('\n',
            $"/ {SelinuxLabel}",
            $"/labeled.txt {SelinuxLabel}",
            $"/dir {SelinuxLabel}"));
        return new F2fsOptions { FileContextsPath = path };
    }

    private static byte[] ExpectedXattrBlock(byte[] value)
    {
        var entryBytes = (4 + SelinuxNameBytes + value.Length + 3) & ~3;
        var block = new byte[XattrHeaderBytes + entryBytes + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), XattrMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), 1);
        block[XattrHeaderBytes] = SecurityXattrIndex;
        block[XattrHeaderBytes + 1] = SelinuxNameBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(XattrHeaderBytes + 2, 2), (ushort)value.Length);
        Encoding.UTF8.GetBytes(SelinuxName).CopyTo(block.AsSpan(XattrHeaderBytes + 4));
        value.CopyTo(block, XattrHeaderBytes + 4 + SelinuxNameBytes);
        return block;
    }

    private static void AssertZeros(byte[] buffer, int offset, int length)
    {
        Assert.True(length >= 0);
        Assert.Equal(-1, buffer.AsSpan(offset, length).IndexOfAnyExcept((byte)0));
    }

    private static byte[] ReadInodeNodeByNid(byte[] image, uint nid)
    {
        for (var block = 0; block + NodeSize <= image.Length; block += NodeSize)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset, 4)) == nid
                && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset + 4, 4)) == nid)
            {
                return image[block..(block + NodeSize)];
            }
        }

        throw new FileNotFoundException($"No F2FS inode node carries nid {nid}.");
    }

    private static byte[] ReadXattrNodeByNid(byte[] image, uint nid)
    {
        for (var block = 0; block + NodeSize <= image.Length; block += NodeSize)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset, 4)) == nid
                && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset + 8, 4)) == XattrNodeFooterFlag)
            {
                return image[block..(block + NodeSize)];
            }
        }

        throw new FileNotFoundException($"No F2FS xattr node carries nid {nid}.");
    }

    private static byte[] ReadInodeNodeByName(byte[] image, string name)
    {
        var encoded = Encoding.UTF8.GetBytes(name);
        for (var block = 0; block + NodeSize <= image.Length; block += NodeSize)
        {
            var nid = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset, 4));
            var ino = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + FooterOffset + 4, 4));
            if (nid == 0 || nid != ino)
            {
                continue;
            }

            var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(block + NameLengthFieldOffset, 4));
            if (nameLength == encoded.Length && image.AsSpan(block + NameFieldOffset, nameLength).SequenceEqual(encoded))
            {
                return image[block..(block + NodeSize)];
            }
        }

        throw new FileNotFoundException($"No F2FS inode node carries the name '{name}'.");
    }

    private static void AssertDirentEntries(byte[] image, int block, bool checkNames)
    {
        var start = block * NodeSize;
        Assert.True(start + DirentEntryAreaOffset + (2 * DirentEntrySize) <= image.Length);
        Assert.Equal(0b11, image[start] & 0b11);

        var dot = start + DirentEntryAreaOffset;
        var dotdot = dot + DirentEntrySize;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dot, 4)));
        Assert.Equal((uint)RootIno, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dot + 4, 4)));
        Assert.Equal(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dot + 8, 2)));
        Assert.Equal(2, (int)image[dot + 10]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dotdot, 4)));
        Assert.Equal((uint)RootIno, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dotdot + 4, 4)));
        Assert.Equal(2, (int)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dotdot + 8, 2)));
        Assert.Equal(2, (int)image[dotdot + 10]);

        if (!checkNames)
        {
            return;
        }

        Assert.Equal((byte)'.', image[start + DirentNameAreaOffset]);
        Assert.Equal("..", Encoding.UTF8.GetString(image, start + DirentNameAreaOffset + 8, 2));
    }

    private sealed class ScratchDir : IDisposable
    {
        public string Path { get; }

        public ScratchDir() => Path = Directory.CreateTempSubdirectory("f2fs_xattr_").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}