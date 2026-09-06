namespace UotanStudio.Firmware.Dex.Decompile;

public enum DecompileOutputMode
{
    PseudoJava,

    BytecodeGuide
}

public sealed class DecompileOptions
{
    public DecompileOutputMode Mode { get; init; } = DecompileOutputMode.PseudoJava;

    public bool ShowBytecodeOffsets { get; init; }

    public static DecompileOptions Default { get; } = new();
}
