namespace UotanStudio.Services.Projects.Schemes;

/// <summary>
/// Portable scheme package that embeds ordered execution stages and optional project-workflow references.
/// <para>可移植的 Scheme 软件包，内嵌有序执行阶段以及可选的项目工作流引用。</para>
/// </summary>
public sealed class SchemePackage
{
    /// <summary>
    /// Scheme format version understood by this implementation.
    /// <para>本实现理解的 Scheme 格式版本。</para>
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// Format version stamped into the serialized package.
    /// <para>写入序列化软件包的格式版本。</para>
    /// </summary>
    /// <value>
    /// The stored version; files without the field read as <see cref="CurrentFormatVersion"/>.
    /// <para>已存储的版本号；缺少该字段的文件按 <see cref="CurrentFormatVersion"/> 读取。</para>
    /// </value>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>
    /// Stable scheme identifier.
    /// <para>稳定的 Scheme 标识符。</para>
    /// </summary>
    /// <value>Identifier unique among the schemes a host exposes.<para>宿主所暴露的各 Scheme 之间唯一的标识符。</para></value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the scheme.
    /// <para>Scheme 的显示名称。</para>
    /// </summary>
    /// <value>Human-readable name shown by hosts.<para>宿主展示的可读名称。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Author-declared scheme version.
    /// <para>作者声明的 Scheme 版本。</para>
    /// </summary>
    /// <value>Informational version string, independent of <see cref="FormatVersion"/>.
    /// <para>说明性版本字符串，与 <see cref="FormatVersion"/> 相互独立。</para></value>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Optional scheme description.
    /// <para>可选的 Scheme 说明。</para>
    /// </summary>
    /// <value>The description text, or <see langword="null"/> when the author supplied none.
    /// <para>说明文本；作者未提供时为 <see langword="null"/>。</para></value>
    public string? Description { get; set; }

    /// <summary>
    /// Optional author name.
    /// <para>可选的作者名称。</para>
    /// </summary>
    /// <value>The author text, or <see langword="null"/> when the author supplied none.
    /// <para>作者文本；作者未提供时为 <see langword="null"/>。</para></value>
    public string? Author { get; set; }

    /// <summary>
    /// Declared scheme inputs supplied by the caller before a run.
    /// <para>运行前由调用方提供的声明式 Scheme 输入。</para>
    /// </summary>
    /// <value>One definition per input; empty when the scheme declares none.<para>每个输入一条定义；未声明时为空列表。</para></value>
    public List<SchemeInputDefinition> Inputs { get; set; } = [];

    /// <summary>
    /// Declared scheme assets addressed relative to the scheme file.
    /// <para>相对 Scheme 文件寻址的声明式资源。</para>
    /// </summary>
    /// <value>One entry per asset; empty when the scheme carries none.<para>每个资源一条记录；未携带时为空列表。</para></value>
    public List<SchemeAsset> Assets { get; set; } = [];

    /// <summary>
    /// Ordered execution stages.
    /// <para>有序的执行阶段。</para>
    /// </summary>
    /// <value>Stages executed in list order.<para>按列表顺序执行的阶段。</para></value>
    public List<SchemeStage> Stages { get; set; } = [];

    /// <summary>
    /// Optional free-form metadata retained verbatim.
    /// <para>可选的原样保留的自由格式元数据。</para>
    /// </summary>
    /// <value>Key/value metadata, or <see langword="null"/> when the scheme carries none.
    /// <para>键值元数据；未携带时为 <see langword="null"/>。</para></value>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Publisher identity bound by <see cref="Signature"/>.
    /// <para>由 <see cref="Signature"/> 绑定的发布者身份。</para>
    /// </summary>
    /// <value>The publisher block with its public key and fingerprint, or <see langword="null"/> for an unsigned package.
    /// <para>携带公钥与指纹的发布者块；未签名包为 <see langword="null"/>。</para></value>
    public VisualPluginPublisher? Publisher { get; set; }

    /// <summary>
    /// Signature over the canonical scheme payload.
    /// <para>覆盖规范化 Scheme 载荷的签名。</para>
    /// </summary>
    /// <value>The algorithm and base64 value, or <see langword="null"/> for an unsigned package.
    /// <para>算法与 Base64 取值；未签名包为 <see langword="null"/>。</para></value>
    public VisualPluginSignature? Signature { get; set; }

    /// <summary>
    /// Whether the package carries both a publisher identity and a non-empty signature.
    /// <para>软件包是否同时携带发布者身份与非空签名。</para>
    /// </summary>
    /// <value><see langword="true"/> when <see cref="Publisher"/> and a complete <see cref="Signature"/> are present.
    /// <para><see cref="Publisher"/> 与完整的 <see cref="Signature"/> 均存在时为 <see langword="true"/>。</para></value>
    public bool IsSigned => Publisher is not null
        && Signature is { Algorithm.Length: > 0, Value.Length: > 0 };

    /// <summary>
    /// Rejects a package stored with a format version this implementation cannot read.
    /// <para>拒绝本实现无法读取的存储格式版本。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// <see cref="FormatVersion"/> is negative or newer than <see cref="CurrentFormatVersion"/>.
    /// <para><see cref="FormatVersion"/> 为负或高于 <see cref="CurrentFormatVersion"/>。</para>
    /// </exception>
    public void EnsureSupportedVersion()
    {
        if (FormatVersion < 0 || FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported scheme format version {FormatVersion}; expected at most {CurrentFormatVersion}.");
        }
    }

    /// <summary>
    /// Repairs optional members that a lenient read may have left <see langword="null"/>.
    /// <para>修复宽容读取可能留下的 <see langword="null"/> 可选成员。</para>
    /// </summary>
    public void Normalize()
    {
        Inputs ??= [];
        Assets ??= [];
        Stages ??= [];
        foreach (var stage in Stages)
        {
            if (stage is null)
            {
                continue;
            }
            stage.Graph ??= new WorkflowSnapshot();
            stage.Inputs ??= [];
            stage.Outputs ??= [];
        }
        Stages.RemoveAll(stage => stage is null);
        Inputs.RemoveAll(input => input is null);
        Assets.RemoveAll(asset => asset is null);
        foreach (var input in Inputs)
        {
            input.Choices ??= [];
        }
    }

    /// <summary>
    /// Validates the structural contract used by the runner.
    /// <para>校验运行器使用的结构契约。</para>
    /// </summary>
    /// <returns>
    /// One English message per violation; an empty list when the package is structurally valid.
    /// <para>每条违规一条英文消息；结构有效时为空列表。</para>
    /// </returns>
    public IReadOnlyList<string> Validate()
    {
        Normalize();
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("The scheme declares no id.");
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("The scheme declares no name.");
        }
        if (Stages.Count == 0)
        {
            errors.Add("The scheme declares no stages.");
        }
        HashSet<string> inputIds = new(StringComparer.Ordinal);
        foreach (var input in Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Id))
            {
                errors.Add("A scheme input declares no id.");
                continue;
            }
            if (!inputIds.Add(input.Id))
            {
                errors.Add($"Duplicate scheme input id '{input.Id}'.");
            }
        }
        HashSet<string> assetIds = new(StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Id))
            {
                errors.Add("A scheme asset declares no id.");
                continue;
            }
            if (!assetIds.Add(asset.Id))
            {
                errors.Add($"Duplicate scheme asset id '{asset.Id}'.");
            }
            if (asset.RelativePath.Length == 0 || Path.IsPathRooted(asset.RelativePath)
                || asset.RelativePath.Replace('\\', '/').Split('/').Any(segment => segment == ".."))
            {
                errors.Add($"Asset '{asset.Id}' must stay inside the scheme directory.");
            }
        }
        HashSet<string> stageIds = new(StringComparer.Ordinal);
        foreach (var stage in Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                errors.Add("A scheme stage declares no id.");
            }
            else if (!stageIds.Add(stage.Id))
            {
                errors.Add($"Duplicate scheme stage id '{stage.Id}'.");
            }
            var hasGraph = stage.Graph is { Nodes.Count: > 0 } || stage.Graph is { Connections.Count: > 0 };
            if (!hasGraph && string.IsNullOrWhiteSpace(stage.ProjectWorkflowName))
            {
                errors.Add($"Stage '{stage.Id}' declares neither an embedded graph nor a project workflow.");
            }
            foreach (var binding in stage.Inputs)
            {
                if (string.IsNullOrWhiteSpace(binding.NodeInstanceId))
                {
                    errors.Add($"Stage '{stage.Id}' binds an input without a node instance id.");
                }
            }
            foreach (var binding in stage.Outputs)
            {
                if (string.IsNullOrWhiteSpace(binding.OutputId) || string.IsNullOrWhiteSpace(binding.NodeInstanceId))
                {
                    errors.Add($"Stage '{stage.Id}' declares an output binding without an output id or node instance id.");
                }
            }
        }
        return errors;
    }
}