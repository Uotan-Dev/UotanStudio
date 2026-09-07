namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Model;

public sealed class CodeItem
{
    public ushort RegistersSize { get; set; }

    public ushort InsSize { get; set; }

    public ushort OutsSize { get; set; }

    public ushort[] RawInsns { get; set; } = [];

    public List<Instruction> Instructions { get; } = [];

    public int InsnsSize => RawInsns.Length;

    public List<TryBlock> Tries { get; } = [];

    public List<CatchHandler> Handlers { get; } = [];

    public byte[]? DebugInfo { get; set; }
}
