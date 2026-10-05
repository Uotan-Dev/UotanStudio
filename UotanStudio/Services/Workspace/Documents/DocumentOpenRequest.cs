using UotanStudio.Firmware.Core;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// <para>统一的文档打开请求：覆盖单文件、对比双路径、设备序列号与对象交接等参数形态。</para>
/// Unified document-open request covering the parameter shapes used across open paths: a single
/// file, a compare pair, a device serial and an object handoff.
/// </summary>
internal sealed record DocumentOpenRequest
{
    /// <summary><para>主文件路径。</para>The primary file path.</summary>
    public required string? Path { get; init; }

    /// <summary><para>请求的打开方式，默认自动判定。</para>The requested open mode; automatic by default.</summary>
    public EditorOpenMode Mode { get; init; } = EditorOpenMode.Auto;

    /// <summary><para>对比文档的第二个路径。</para>The second path for compare documents.</summary>
    public string? SecondaryPath { get; init; }

    /// <summary><para>对比文档的附加路径。</para>Additional paths for compare documents.</summary>
    public IReadOnlyList<string> AdditionalPaths { get; init; } = [];

    /// <summary><para>投屏类文档的设备序列号。</para>The device serial for mirror-style documents.</summary>
    public string? Serial { get; init; }

    /// <summary><para>对象交接类文档（如节点工程）的负载。</para>The payload for object-handoff documents such as node projects.</summary>
    public object? Payload { get; init; }

    /// <summary><para>自动模式下已完成的格式检测结果，避免二次探测。</para>The format detection result resolved in automatic mode, reused to avoid re-probing.</summary>
    public FormatDetectionResult? Detection { get; init; }
}