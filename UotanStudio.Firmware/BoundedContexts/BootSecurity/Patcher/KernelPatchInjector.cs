using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;

/// <summary>
/// <para>KernelPatch/APatch 镜像部分的托管实现：准备 kpimg 预设、追加到 Android arm64 Image 并把入口重定向到 KernelPatch 跳板。</para>
/// Native managed implementation of the image part of KernelPatch/APatch. It prepares the kpimg preset, appends it to an Android arm64 Image and redirects the entry point to the KernelPatch trampoline.
/// </summary>
internal static class KernelPatchInjector
{
    private const int PageAlignment = 0x1000;
    private const int SetupOffset = 0x40;
    private const int SetupKimgSize = SetupOffset + 0x08;
    private const int SetupKpimgSize = SetupOffset + 0x10;
    private const int SetupKernelSize = SetupOffset + 0x18;
    private const int SetupPageShift = SetupOffset + 0x20;
    private const int SetupImageOffset = SetupOffset + 0x28;
    private const int SetupStartOffset = SetupOffset + 0x30;
    private const int SetupExtraSize = SetupOffset + 0x38;
    private const int SetupHeaderBackup = SetupOffset + 0xA0;
    private const int SetupSuperKey = SetupOffset + 0xA8;
    private const int SetupRootSuperKey = SetupOffset + 0xE8;
    // KernelPatch appends a zeroed patch_extra_item_t sentinel so the extra
    // region has a clearly terminated tail that kptools can rescan.
    private const int ExtraItemSentinelSize = 0x80;

    /// <summary>
    /// <para>把 KernelPatch kpimg 追加到内核镜像末尾并重定向入口到 KernelPatch 跳板，返回组合后的镜像。</para>
    /// Appends the kpimg to the end of the kernel image, redirects the entry point to the KernelPatch trampoline and returns the combined image.
    /// </summary>
    /// <param name="kernel">原始 Android arm64 Image 内核字节。</param>
    /// <param name="kpimg">KernelPatch 补丁镜像字节。</param>
    /// <param name="superKey">用于生成 Supervisor/root 密钥的超级密码。</param>
    /// <returns>组合后的补丁内核镜像字节。</returns>
    /// <exception cref="InvalidDataException">内核过小、缺少 kallsyms、已被补丁或 kpimg 无效时抛出。</exception>
    /// <exception cref="NotSupportedException">kpimg 目标架构或内核格式不受支持时抛出。</exception>
    /// <remarks>
    /// <para>image_size 描述内存中的镜像大小，可能超过引导镜像实际存在的字节数，故只用于文件内前缀；kernel_size 与 arm64 Image 头部同字段一致，而不采用裁剪后的文件前缀。</para>
    /// <para>start_offset 是地址空间值，指向取整后 kernel_size 之后的内核区结尾；当末尾附加项把文件推过该点时，以末尾对齐为准。</para>
    /// <para>The in-memory image_size can exceed the bytes actually present, so it is used only as an in-file prefix; kernel_size mirrors the arm64 Image header field rather than the trimmed prefix, and start_offset is an address-space value where the tail alignment wins once extra items push the file past it.</para>
    /// </remarks>
    public static byte[] Patch(ReadOnlySpan<byte> kernel, ReadOnlySpan<byte> kpimg, string superKey)
    {
        if (kernel.Length < 0x40) throw new InvalidDataException("Kernel image is too small for APatch.");
        if (!HasKallsyms(kernel))
            throw new InvalidDataException("APatch requires CONFIG_KALLSYMS=y in the kernel.");
        if (kernel.IndexOf("KP1158"u8) >= 0)
            throw new InvalidDataException("The kernel already contains a KernelPatch image; start from the original kernel.");
        if (kpimg.Length < 0x1000 || !kpimg[..6].SequenceEqual("KP1158"u8))
            throw new InvalidDataException("The selected APatch package does not contain a valid kpimg.");
        var kpConfig = BinaryPrimitives.ReadUInt64LittleEndian(kpimg[0x10..]);
        if ((kpConfig & 0x4) != 0)
            throw new NotSupportedException("The selected APatch kpimg targets x86_64, while this kernel is arm64.");

        var entryOffset = kernel[..2].SequenceEqual("MZ"u8) ? 4 : 0;
        if (!TryReadArm64Header(kernel, entryOffset, out var imageSize, out var pageShift))
            throw new NotSupportedException("APatch currently supports Android arm64 Image kernels only.");

        var originalLength = imageSize is > 0 and <= int.MaxValue && imageSize <= (ulong)kernel.Length
            ? (int)imageSize : kernel.Length;
        if (originalLength < 0x40)
            throw new InvalidDataException("The arm64 kernel image is too small.");
        var alignedKernelLength = Align(originalLength, PageAlignment);
        var alignedKpimgLength = Align(kpimg.Length, 0x10);
        var output = new byte[checked(alignedKernelLength + alignedKpimgLength + ExtraItemSentinelSize)];
        kernel[..originalLength].CopyTo(output);
        kpimg.CopyTo(output.AsSpan(alignedKernelLength));

        var preset = output.AsSpan(alignedKernelLength, alignedKpimgLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupKimgSize..], originalLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupKpimgSize..], alignedKpimgLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupKernelSize..], (long)imageSize);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupPageShift..], pageShift);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupImageOffset..], alignedKernelLength);
        long startOffset = Align64(imageSize, PageAlignment);
        var outAllLength = checked(alignedKernelLength + alignedKpimgLength + ExtraItemSentinelSize);
        if (outAllLength > startOffset) startOffset = Align(outAllLength, PageAlignment);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupStartOffset..], startOffset);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupExtraSize..], ExtraItemSentinelSize);
        kernel[..Math.Min(8, kernel.Length)].CopyTo(preset[SetupHeaderBackup..]);
        var keyBytes = Encoding.UTF8.GetBytes(superKey);
        if (keyBytes.Length is 0 or >= 0x40) throw new InvalidDataException("APatch SuperKey is too long.");
        if (superKey.Equals("su", StringComparison.Ordinal))
        {
            keyBytes.CopyTo(preset[SetupSuperKey..]);
            preset[SetupSuperKey + keyBytes.Length] = 0;
        }
        else
        {
            SHA256.HashData(keyBytes).CopyTo(preset[SetupRootSuperKey..]);
            preset[SetupSuperKey..(SetupSuperKey + 0x40)].Clear();
        }

        var target = checked(alignedKernelLength + PageAlignment);
        var branch = EncodeBranch(entryOffset, target);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(entryOffset, 4), branch);
        return output;
    }

    private static bool TryReadArm64Header(ReadOnlySpan<byte> kernel, int entryOffset, out ulong imageSize, out int pageShift)
    {
        imageSize = 0;
        pageShift = 12;
        if (kernel.Length < 0x40 || !kernel.Slice(0x38, 4).SequenceEqual("ARM\x64"u8)) return false;
        imageSize = BinaryPrimitives.ReadUInt64LittleEndian(kernel[0x10..]);
        var flags = BinaryPrimitives.ReadUInt64LittleEndian(kernel[0x18..]) & 0x0f;
        pageShift = ((flags >> 1) & 0x3) switch { 2 => 14, 3 => 16, _ => 12 };
        var instruction = BinaryPrimitives.ReadUInt32LittleEndian(kernel[entryOffset..]);
        return (instruction & 0xfc000000) == 0x14000000;
    }

    private static uint EncodeBranch(int from, int to)
    {
        var delta = (long)to - from;
        if ((delta & 3) != 0 || delta < -(128L * 1024 * 1024) || delta >= 128L * 1024 * 1024)
            throw new InvalidDataException("KernelPatch trampoline is outside the arm64 branch range.");
        return 0x14000000u | ((uint)(delta >> 2) & 0x03ffffffu);
    }

    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static long Align64(ulong value, int alignment) => checked(((long)((value + (ulong)alignment - 1) / (ulong)alignment)) * alignment);

    private static bool HasKallsyms(ReadOnlySpan<byte> kernel)
    {
        if (kernel.IndexOf("CONFIG_KALLSYMS=y"u8) >= 0) return true;
        var start = kernel.IndexOf("IKCFG_ST"u8);
        if (start < 0) return false;
        start += "IKCFG_ST"u8.Length;
        var endRelative = kernel[start..].IndexOf("IKCFG_ED"u8);
        if (endRelative <= 0) return false;
        try
        {
            using var input = new MemoryStream(kernel.Slice(start, endRelative).ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var config = new MemoryStream();
            gzip.CopyTo(config);
            return config.GetBuffer().AsSpan(0, checked((int)config.Length)).IndexOf("CONFIG_KALLSYMS=y"u8) >= 0;
        }
        catch (InvalidDataException) { return false; }
    }
}