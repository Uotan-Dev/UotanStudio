namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Models;

public class PayloadProperties
{
    public string FileHash { get; internal set; } = string.Empty;

    public long FileSize { get; internal set; }

    public string MetadataHash { get; internal set; } = string.Empty;

    public long MetadataSize { get; internal set; }

    public IReadOnlyDictionary<string, string> Properties { get; internal set; } = new Dictionary<string, string>();
}
