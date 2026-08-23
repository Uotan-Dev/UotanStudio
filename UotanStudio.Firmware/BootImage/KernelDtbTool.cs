namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// 内核 + DTB 合并/提取工具。
/// <para>
/// Kernel + DTB merge/extract tool.</para>
/// <para>
/// 该工具依赖完整的 Device Tree 解析能力；本移植版不内联 DTB 解析，仅保留 API 占位。
/// 如需 DT 处理，请使用本项目独立的 DTB 格式实现（如有）。</para>
/// <para>
/// The full tool requires complete Device Tree parsing; this simplified port does not inline
/// DTB parsing and only keeps the API surface as a placeholder.</para>
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
    /// 提取内核数据。
    /// <para>Extracts the kernel data.</para>
    /// </summary>
    /// <param name="data">合并数据。<para>The merged data.</para></param>
    /// <returns>内核数据；不支持时返回空数组。<para>The kernel data, or an empty array when unsupported.</para></returns>
    public static byte[] ExtractKernel(byte[] data)
    {
        throw new NotSupportedException(
            "KernelDtbTool requires full Device Tree parsing which is not inlined in this simplified port. " +
            "Use the dedicated DTB implementation in this project when available.");
    }

    /// <summary>
    /// 提取第一个 DTB。
    /// <para>Extracts the first DTB.</para>
    /// </summary>
    /// <param name="data">合并数据。<para>The merged data.</para></param>
    /// <returns>DTB 数据；不支持时返回空数组。<para>The DTB data, or an empty array when unsupported.</para></returns>
    public static byte[] ExtractFirstDtb(byte[] data)
    {
        throw new NotSupportedException(
            "KernelDtbTool requires full Device Tree parsing which is not inlined in this simplified port.");
    }
}