namespace UotanStudio.Firmware.BlockImage;

/// <summary>
/// Raw magic words shared by the partition-table detectors in this assembly.
/// <para>本程序集中分区表检测器共用的原始魔数。</para>
/// </summary>
internal static class PartitionTableSignatures
{
    /// <summary>Amlogic vendor partition-table magic (<c>AMLPART!</c>). <para>Amlogic 厂商分区表魔数（<c>AMLPART!</c>）。</para></summary>
    internal static ReadOnlySpan<byte> AmlogicMagic => "AMLPART!"u8;
}