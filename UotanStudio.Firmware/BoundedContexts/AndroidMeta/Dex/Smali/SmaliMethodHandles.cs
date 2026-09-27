namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

internal static class SmaliMethodHandles
{
    private static readonly string[] Names =
    [
        "static-put", "static-get", "instance-put", "instance-get", "invoke-static",
        "invoke-instance", "invoke-constructor", "invoke-direct", "invoke-interface"
    ];

    internal static ushort Kind(string name)
    {
        var index = Array.IndexOf(Names, name);
        return index >= 0 ? (ushort)index : throw new FormatException($"Unknown method-handle kind '{name}'.");
    }

    internal static string Name(ushort kind)
        => kind < Names.Length ? Names[kind] : throw new InvalidDataException("Invalid method-handle kind.");
}
