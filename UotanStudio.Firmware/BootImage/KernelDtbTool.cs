using UotanStudio.Firmware.Dt;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// 内核 + DTB 合并/提取工具。
/// <para>
/// Kernel + DTB merge/extract tool.</para>
/// <para>
/// 复用本项目的 <c>UotanStudio.Firmware.Dt</c> FDT 解析能力，从 magiskboot 合并格式中拆分内核与 DTB。</para>
/// <para>
/// Reuses the <c>UotanStudio.Firmware.Dt</c> FDT parsing to split kernels and DTBs from the magiskboot merge format.</para>
/// </summary>
public static class KernelDtbTool
{
    /// <summary>
    /// 内核 + DTB 组件。
    /// <para>A kernel + DTB component.</para>
    /// </summary>
    public class KernelDtbComponent
    {
        /// <summary>
        /// 组件名称。
        /// <para>The component name.</para>
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 组件数据。
        /// <para>The component data.</para>
        /// </summary>
        public byte[] Data { get; set; } = [];

        /// <summary>
        /// 组件描述。
        /// <para>The component description.</para>
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// 内核 DTB 类型。
    /// <para>Kernel DTB type.</para>
    /// </summary>
    public enum KernelDtbType
    {
        /// <summary>
        /// 未知。
        /// <para>Unknown.</para>
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// 内核与 DTB 未合并（分开存放）。
        /// <para>Kernel and DTB are separate.</para>
        /// </summary>
        Separate = 1,

        /// <summary>
        /// 内核与 DTB 已合并（magiskboot 的 merge 格式）。
        /// <para>Kernel and DTB merged (magiskboot merge format).</para>
        /// </summary>
        Merged = 2
    }

    /// <summary>
    /// 提取内核数据（合并格式中首个有效 FDT 之前的部分）。
    /// <para>Extracts the kernel data (the part before the first valid FDT in the merged format).</para>
    /// </summary>
    /// <param name="data">合并数据。<para>The merged data.</para></param>
    /// <returns>内核数据；未检测到有效 FDT 时返回空数组。<para>The kernel data, or an empty array when no valid FDT is detected.</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> 为 <c>null</c>。<para><paramref name="data"/> is <c>null</c>.</para></exception>
    public static byte[] ExtractKernel(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return TryFindFdt(data, out var offset, out _) && offset > 0
            ? data.AsSpan(0, offset).ToArray()
            : [];
    }

    /// <summary>
    /// 提取第一个 DTB（合并格式中的首个有效 FDT）。
    /// <para>Extracts the first DTB (the first valid FDT in the merged format).</para>
    /// </summary>
    /// <param name="data">合并数据。<para>The merged data.</para></param>
    /// <returns>DTB 数据；未检测到有效 FDT 时返回空数组。<para>The DTB data, or an empty array when no valid FDT is detected.</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> 为 <c>null</c>。<para><paramref name="data"/> is <c>null</c>.</para></exception>
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