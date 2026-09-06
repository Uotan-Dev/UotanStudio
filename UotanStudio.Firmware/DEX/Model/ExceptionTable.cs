namespace UotanStudio.Firmware.Dex.Model;

public sealed class CatchHandlerEntry
{
    public int TypeIndex { get; set; }

    public int Address { get; set; }
}

public sealed class CatchHandler
{
    public List<CatchHandlerEntry> Entries { get; } = [];

    public int CatchAllAddress { get; set; } = -1;

    public bool HasCatchAll => CatchAllAddress >= 0;
}

public sealed class TryBlock
{
    public int StartAddress { get; set; }

    public ushort InsnCount { get; set; }

    public int HandlerIndex { get; set; }

    public int EndAddress => StartAddress + InsnCount;
}
