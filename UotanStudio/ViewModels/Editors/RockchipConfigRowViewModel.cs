using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// One browse row of an RKDevTool download-list configuration: the stored name, the address read from the
/// trailing block, the image path and whether that path still exists on disk.
/// <para>RKDevTool 下载列表配置的一行浏览数据：存储的名称、尾块中读出的地址、镜像路径，以及该路径是否仍存在。</para>
/// </summary>
public sealed partial class RockchipConfigRowViewModel : ObservableObject
{
    /// <summary>
    /// Creates a row from a parsed configuration entry.
    /// <para>由解析出的配置条目创建一行。</para>
    /// </summary>
    /// <param name="entry">Parsed entry.<para>解析出的条目。</para></param>
    public RockchipConfigRowViewModel(RockchipConfigEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Name = entry.Name;
        Address = entry.Address;
        AddressText = $"0x{entry.Address:X16}";
        Path = entry.Path;
        TailText = Convert.ToHexString(entry.Trailing);
        FileExists = entry.Path.Length > 0 && File.Exists(entry.Path);
    }

    /// <summary>Gets the stored row name.<para>获取存储的行名称。</para></summary>
    public string Name { get; }

    /// <summary>Gets the address from the trailing block.<para>获取尾块中的地址。</para></summary>
    public ulong Address { get; }

    /// <summary>Gets the address formatted as a 64-bit hexadecimal value.<para>获取格式化为 64 位十六进制的地址。</para></summary>
    public string AddressText { get; }

    /// <summary>Gets the stored image path.<para>获取存储的镜像路径。</para></summary>
    public string Path { get; }

    /// <summary>Gets the complete trailing block as hexadecimal text.<para>获取完整尾块的十六进制文本。</para></summary>
    public string TailText { get; }

    /// <summary>Gets whether the stored path exists on this machine.<para>获取存储路径在本机是否存在。</para></summary>
    public bool FileExists { get; }
}