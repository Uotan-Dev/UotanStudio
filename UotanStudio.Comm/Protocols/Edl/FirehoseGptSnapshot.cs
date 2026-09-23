using UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Validated GPT observation bound to one protocol instance, physical region and configured slot.</summary>
public sealed class FirehoseGptSnapshot
{
    internal FirehoseGptSnapshot(FirehoseProtocol owner, FirehoseStorageGeometry geometry, int slot, GptTable table)
    {
        Owner = owner;
        Geometry = geometry;
        Slot = slot;
        Table = table;
    }

    internal FirehoseProtocol Owner { get; }
    /// <summary>Gets the physical region and geometry reported for this observation.</summary>
    public FirehoseStorageGeometry Geometry { get; }
    /// <summary>Gets the configured slot used during acquisition; zero means no slot attribute.</summary>
    public int Slot { get; }
    /// <summary>Gets both validated GPT copies represented as one table, without repair or fallback.</summary>
    public GptTable Table { get; }
}