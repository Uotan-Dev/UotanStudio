using UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A validated GPT observation. It does not authorize later writes or prove the table is still current.</summary>
public sealed class MtkGptSnapshot
{
    internal MtkGptSnapshot(MtkXFlashStorageInfo storage, uint partitionId, GptTable table)
    {
        Storage = storage;
        PartitionId = partitionId;
        Table = table;
    }

    /// <summary>Gets the exact discovered storage snapshot used for the read.</summary>
    public MtkXFlashStorageInfo Storage { get; }
    /// <summary>Gets the physical region containing the table.</summary>
    public uint PartitionId { get; }
    /// <summary>Gets the validated partition metadata.</summary>
    public GptTable Table { get; }
}