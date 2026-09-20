namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;

public sealed class Nb0Metadata
{

    public int EntryCount { get; set; }

    public long TotalSize { get; set; }

    public long DataSectionOffset { get; set; }

    public string? InferredFirmwareType { get; set; }

    public string? InferredDeviceModel { get; set; }

    public string? InferredVersion { get; set; }

    public List<Nb0FileEntry> Entries { get; set; } = [];

    public List<Nb0Md5Record> Md5Records { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public bool HasMd5Records => Md5Records.Count > 0;

    public Dictionary<uint, Nb0Md5Record>? Md5RecordByOffset
    {
        get
        {
            if (field is null && Md5Records.Count > 0)
            {
                BuildMd5RecordIndex();
            }
            return field;
        }

        private set;
    }

    /// <summary>
    /// Builds the MD5 record dictionary indexed by offset.
    /// <para>构建按偏移量索引的 MD5 校验记录字典。</para>
    /// </summary>
    public void BuildMd5RecordIndex()
    {
        var dict = new Dictionary<uint, Nb0Md5Record>(Md5Records.Count);
        foreach (var record in Md5Records)
        {
            dict.TryAdd(record.Offset, record);
        }
        Md5RecordByOffset = dict;
    }
}