using System;
using System.Collections.Generic;

namespace UotanStudio.Core.Projects;

/// <summary>
/// <para>固件工程文件模型（.uotproj）的核心数据载体。</para>
/// Core data carrier of the firmware project file (.uotproj).
/// </summary>
/// <remarks>
/// <para>序列化为 JSON，携带 schema 版本以支持未来演进；包含部件清单、目标设备与默认模式。</para>
/// Serialized as JSON with a schema version for forward evolution; carries the component
/// list, target device and default mode.
/// </remarks>
public class FirmwareProject
{
    /// <summary>
    /// <para>工程文件 Schema 版本，未来格式演进时用于迁移。</para>
    /// Project file schema version, used for migration on future format changes.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// <para>获取或设置工程名称。</para>
    /// Gets or sets the project name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置工程根目录（绝对路径，文件所在目录）。</para>
    /// Gets or sets the project root directory (absolute; the directory holding the file).
    /// </summary>
    public string RootDirectory { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置目标设备型号（可选，用于刷写校验）。</para>
    /// Gets or sets the target device model (optional; used for flash pre-checks).
    /// </summary>
    public string TargetModel { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置默认操作模式（ADB/Fastboot/EDL/RKDL）。</para>
    /// Gets or sets the default operation mode (ADB/Fastboot/EDL/RKDL).
    /// </summary>
    public string DefaultMode { get; set; } = "Fastboot";

    /// <summary>
    /// <para>获取或设置部件清单：相对工程根目录的路径列表。</para>
    /// Gets or sets the component list: paths relative to the project root.
    /// </summary>
    public List<string> Components { get; set; } = [];

    /// <summary>
    /// <para>获取或设置最近打开时间（UTC），用于"最近工程"排序。</para>
    /// Gets or sets the last-opened time (UTC), used to order "recent projects".
    /// </summary>
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}