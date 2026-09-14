using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;

/// <summary>
/// Native managed implementation of the image part of KernelPatch/APatch.
/// It prepares the kpimg preset, appends it to an Android arm64 Image and
/// redirects the image entry point to the KernelPatch trampoline.
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
    private const int SetupHeaderBackup = SetupOffset + 0xA0;
    private const int SetupSuperKey = SetupOffset + 0xA8;
    private const int SetupRootSuperKey = SetupOffset + 0xE8;

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

        // image_size describes the in-memory image and can exceed the bytes
        // present in a boot image. Use it only for an in-file prefix.
        var originalLength = imageSize is > 0 and <= int.MaxValue && imageSize <= (ulong)kernel.Length
            ? (int)imageSize : kernel.Length;
        if (originalLength < 0x40)
            throw new InvalidDataException("The arm64 kernel image is too small.");
        var alignedKernelLength = Align(originalLength, PageAlignment);
        var alignedKpimgLength = Align(kpimg.Length, 0x10);
        var output = new byte[checked(alignedKernelLength + alignedKpimgLength)];
        kernel[..originalLength].CopyTo(output);
        kpimg.CopyTo(output.AsSpan(alignedKernelLength));

        var preset = output.AsSpan(alignedKernelLength, alignedKpimgLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupKimgSize..], originalLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupKpimgSize..], alignedKpimgLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupKernelSize..], originalLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupPageShift..], pageShift);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupImageOffset..], alignedKernelLength);
        BinaryPrimitives.WriteInt64LittleEndian(preset[SetupStartOffset..], checked(alignedKernelLength + PageAlignment));
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