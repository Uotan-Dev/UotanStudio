namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Writing;

public sealed class WriteOptions
{
    public bool IncludeTrailer { get; init; } = true;

    public bool CpioSymlinkAsDataPayload { get; init; } = true;

    public bool RenumberInodes { get; init; }

    public bool IgnoreDeviceNumbers { get; init; }

    public uint? OwnerUserId { get; init; }

    public uint? OwnerGroupId { get; init; }
}