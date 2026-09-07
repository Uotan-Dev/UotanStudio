using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public static class KernelDtbTool
{
    public class KernelDtbComponent
    {
        public string Name { get; set; } = string.Empty;

        public byte[] Data { get; set; } = [];

        public string Description { get; set; } = string.Empty;
    }

    public enum KernelDtbType
    {
        Unknown = 0,

        Separate = 1,

        Merged = 2
    }

    /// <summary>
    /// Extracts the kernel data (the part before the first valid FDT in the merged format).
    /// <para>提取内核数据（合并格式中首个有效 FDT 之前的部分）。</para>
    /// </summary>
    public static byte[] ExtractKernel(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return TryFindFdt(data, out var offset, out _) && offset > 0
            ? data.AsSpan(0, offset).ToArray()
            : [];
    }

    /// <summary>
    /// Extracts the first DTB (the first valid FDT in the merged format).
    /// <para>提取第一个 DTB（合并格式中的首个有效 FDT）。</para>
    /// </summary>
    public static byte[] ExtractFirstDtb(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return TryFindFdt(data, out var offset, out var header)
            ? data.AsSpan(offset, checked((int)header.TotalSize)).ToArray()
            : [];
    }

    private static bool TryFindFdt(ReadOnlySpan<byte> data, out int offset, out FdtHeader header)
    {
        offset = 0;
        header = new();

        for (var i = 0; i + FdtConstants.HeaderSize <= data.Length; i++)
        {
            if (ReadUInt32BigEndian(data.Slice(i, 4)) != FdtConstants.FdtMagic)
            {
                continue;
            }

            FdtHeader candidate;
            try
            {
                candidate = FdtHeader.ReadFrom(data[i..]);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (candidate.Version < FdtConstants.FdtLastCompVersion || candidate.TotalSize < FdtConstants.HeaderSize)
            {
                continue;
            }

            if (candidate.TotalSize > data.Length - i)
            {
                continue;
            }

            offset = i;
            header = candidate;
            return true;
        }

        return false;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data) =>
        (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
}
