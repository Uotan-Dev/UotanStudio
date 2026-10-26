namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// One CRC-verified GPT table copy inside an image, reported without exposing its entries.
/// <para>镜像内一份 CRC 校验通过的 GPT 表副本，只上报定位信息而不暴露条目。</para>
/// </summary>
/// <param name="HeaderLba">Logical block address of the copy's header. <para>该副本头部的逻辑块地址。</para></param>
/// <param name="EntryCount">Declared entry slots of the copy. <para>该副本声明的条目槽位数。</para></param>
/// <param name="PartitionCount">Populated partitions read from its entry array. <para>从其条目数组读到的已用分区数。</para></param>
public readonly record struct GptTableCopy(ulong HeaderLba, uint EntryCount, int PartitionCount);