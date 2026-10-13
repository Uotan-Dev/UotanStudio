using UotanStudio.Firmware.Core;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// Unified document-open request covering the parameter shapes used across open paths: a single
/// file, a compare pair, a device serial and an object handoff.
/// <para>统一的文档打开请求：覆盖单文件、对比双路径、设备序列号与对象交接等参数形态。</para>
/// </summary>
internal sealed record DocumentOpenRequest
{
    /// <summary>The primary file path. <para>主文件路径。</para></summary>
    /// <value>The main target path, or null. <para>主目标路径，或为 null。</para></value>
    public required string? Path { get; init; }

    /// <summary>The requested open mode; automatic by default. <para>请求的打开方式，默认自动判定。</para></summary>
    /// <value>The mode to open with. <para>打开时采用的方式。</para></value>
    public EditorOpenMode Mode { get; init; } = EditorOpenMode.Auto;

    /// <summary>The second path for compare documents. <para>对比文档的第二个路径。</para></summary>
    /// <value>The compare partner path, or null. <para>对比对象路径，或为 null。</para></value>
    public string? SecondaryPath { get; init; }

    /// <summary>Additional paths for compare documents. <para>对比文档的附加路径。</para></summary>
    /// <value>The extra compare paths, empty when there are none. <para>附加对比路径，无则为空集合。</para></value>
    public IReadOnlyList<string> AdditionalPaths { get; init; } = [];

    /// <summary>The device serial for mirror-style documents. <para>投屏类文档的设备序列号。</para></summary>
    /// <value>The device serial, or null. <para>设备序列号，或为 null。</para></value>
    public string? Serial { get; init; }

    /// <summary>The payload for object-handoff documents such as node projects. <para>对象交接类文档（如节点工程）的负载。</para></summary>
    /// <value>The handed-off object, or null. <para>交接的对象，或为 null。</para></value>
    public object? Payload { get; init; }

    /// <summary>The format detection result resolved in automatic mode, reused to avoid re-probing. <para>自动模式下已完成的格式检测结果，避免二次探测。</para></summary>
    /// <value>The detection result to reuse, or null. <para>待复用的检测结果，或为 null。</para></value>
    public FormatDetectionResult? Detection { get; init; }
}