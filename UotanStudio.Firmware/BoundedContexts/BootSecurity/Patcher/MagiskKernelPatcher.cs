namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;

/// <summary>Applies the source-faithful, pattern based kernel fixes used by Magisk patchers.</summary>
internal static class MagiskKernelPatcher
{
    private static readonly (string Find, string Replace, string Name)[] CommonPatches =
    [
        ("49010054011440B93FA00F71E9000054010840B93FA00F7189000054001840B91FA00F7188010054",
            "A1020054011440B93FA00F7140020054010840B93FA00F71E0010054001840B91FA00F7181010054", "remove Samsung RKP"),
        ("821B8012", "E2FF8F12", "remove Samsung defex"),
        ("006044B91F040071802F005460DE41F9", "006044B91F00006B802F005460DE41F9", "remove Samsung defex A8 variant"),
        ("603A46B91F0400710030005460C642F9", "603A46B91F00006B0030005460C642F9", "remove Samsung defex N9 variant"),
        ("70726F63615F636F6E66696700", "70726F63615F6D616769736B00", "disable Samsung PROCA")
    ];

    public static byte[] Apply(ReadOnlySpan<byte> kernel, bool forceRootfs, List<string> changes)
    {
        var output = kernel.ToArray();
        foreach (var (find, replace, name) in CommonPatches)
        {
            if (TryHexPatch(output, find, replace)) changes.Add($"Magisk kernel: {name}");
        }
        if (forceRootfs && TryHexPatch(output, "736B69705F696E697472616D667300", "77616E745F696E697472616D667300"))
            changes.Add("Magisk kernel: force legacy rootfs initialization");
        return output;
    }

    private static bool TryHexPatch(Span<byte> image, string find, string replace)
    {
        var pattern = Convert.FromHexString(find);
        var replacement = Convert.FromHexString(replace);
        if (replacement.Length > pattern.Length) return false;
        var offset = image.IndexOf(pattern);
        if (offset < 0) return false;
        replacement.CopyTo(image[offset..]);
        if (replacement.Length < pattern.Length) image[(offset + replacement.Length)..(offset + pattern.Length)].Clear();
        return true;
    }
}