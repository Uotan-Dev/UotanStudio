using System.Buffers;

namespace UotanStudio.Firmware.BootSecurity.Patcher.Kernel;

/// <summary>Applies the source-faithful, pattern based kernel fixes for Magisk-style patching.</summary>
/// <remarks>
/// Every pattern is located by the same single pass over the kernel; the sequential search that used one full
/// image scan per pattern produced the same first occurrences because no replacement can create a later pattern.
/// </remarks>
internal static class MagiskKernelPatcher
{
    private static ReadOnlySpan<byte> SkipInitramfs => "skip_initramfs"u8;

    private static readonly (byte[] Find, byte[] Replace, string Name)[] CommonPatches =
    [
        (Convert.FromHexString("49010054011440B93FA00F71E9000054010840B93FA00F7189000054001840B91FA00F7188010054"),
            Convert.FromHexString("A1020054011440B93FA00F7140020054010840B93FA00F71E0010054001840B91FA00F7181010054"), "remove Samsung RKP"),
        (Convert.FromHexString("821B8012"), Convert.FromHexString("E2FF8F12"), "remove Samsung defex"),
        (Convert.FromHexString("006044B91F040071802F005460DE41F9"),
            Convert.FromHexString("006044B91F00006B802F005460DE41F9"), "remove Samsung defex A8 variant"),
        (Convert.FromHexString("603A46B91F0400710030005460C642F9"),
            Convert.FromHexString("603A46B91F00006B0030005460C642F9"), "remove Samsung defex N9 variant"),
        (Convert.FromHexString("70726F63615F636F6E66696700"),
            Convert.FromHexString("70726F63615F6D616769736B00"), "disable Samsung PROCA")
    ];

    private static readonly byte[] ForceFind = Convert.FromHexString("736B69705F696E697472616D667300");
    private static readonly byte[] ForceReplace = Convert.FromHexString("77616E745F696E697472616D667300");
    private static readonly SearchValues<byte> PatchFirstBytes = CreateFirstBytes();

    public static bool NeedsLegacyRootfs(ReadOnlySpan<byte> kernel)
        => kernel.IndexOf(SkipInitramfs) >= 0;

    public static byte[] Apply(ReadOnlySpan<byte> kernel, bool forceRootfs, List<string> changes)
        => Apply(kernel, forceRootfs, detectRootfs: false, changes);

    /// <summary>
    /// Applies the same fixes and enables the legacy rootfs rewrite whenever the kernel already carries
    /// <c>skip_initramfs</c>, matching <see cref="NeedsLegacyRootfs"/> without a second full image scan.
    /// </summary>
    public static byte[] ApplyDetectingRootfs(ReadOnlySpan<byte> kernel, bool forceRootfs, List<string> changes)
        => Apply(kernel, forceRootfs, detectRootfs: true, changes);

    private static byte[] Apply(ReadOnlySpan<byte> kernel, bool forceRootfs, bool detectRootfs, List<string> changes)
    {
        var output = kernel.ToArray();
        var offsets = new int[CommonPatches.Length + 1];
        Array.Fill(offsets, -1);
        var remaining = offsets.Length;
        for (var index = 0; index < output.Length && remaining > 0; index++)
        {
            if (!PatchFirstBytes.Contains(output[index]))
                continue;
            for (var patch = 0; patch < CommonPatches.Length; patch++)
            {
                if (offsets[patch] >= 0)
                    continue;
                var find = CommonPatches[patch].Find;
                if (index > output.Length - find.Length || !output.AsSpan(index, find.Length).SequenceEqual(find))
                    continue;
                offsets[patch] = index;
                remaining--;
            }
            if (offsets[^1] < 0 && index <= output.Length - ForceFind.Length
                && output.AsSpan(index, ForceFind.Length).SequenceEqual(ForceFind))
            {
                offsets[^1] = index;
                remaining--;
            }
        }

        for (var patch = 0; patch < CommonPatches.Length; patch++)
        {
            if (offsets[patch] < 0)
                continue;
            var (find, replace, name) = CommonPatches[patch];
            if (ApplyAt(output, offsets[patch], find, replace) || TryHexPatch(output, find, replace))
            {
                changes.Add($"Magisk kernel: {name}");
            }
        }

        if ((forceRootfs || (detectRootfs && offsets[^1] >= 0)) && offsets[^1] >= 0)
        {
            if (ApplyAt(output, offsets[^1], ForceFind, ForceReplace)
                || TryHexPatch(output, ForceFind, ForceReplace))
            {
                changes.Add("Magisk kernel: force legacy rootfs initialization");
            }
        }
        return output;
    }

    private static bool ApplyAt(Span<byte> image, int offset, byte[] pattern, byte[] replacement)
    {
        if (offset < 0 || offset > image.Length - pattern.Length || !image.Slice(offset, pattern.Length).SequenceEqual(pattern))
            return false;
        replacement.CopyTo(image[offset..]);
        if (replacement.Length < pattern.Length)
            image[(offset + replacement.Length)..(offset + pattern.Length)].Clear();
        return true;
    }

    private static bool TryHexPatch(Span<byte> image, byte[] pattern, byte[] replacement)
    {
        if (replacement.Length > pattern.Length) return false;
        var offset = image.IndexOf(pattern);
        if (offset < 0) return false;
        replacement.CopyTo(image[offset..]);
        if (replacement.Length < pattern.Length) image[(offset + replacement.Length)..(offset + pattern.Length)].Clear();
        return true;
    }

    private static SearchValues<byte> CreateFirstBytes()
    {
        Span<byte> first = stackalloc byte[CommonPatches.Length + 1];
        for (var index = 0; index < CommonPatches.Length; index++)
            first[index] = CommonPatches[index].Find[0];
        first[^1] = ForceFind[0];
        return SearchValues.Create(first);
    }
}