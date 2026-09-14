using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Delivery;

/// <summary>One immutable file or partition in a delivery snapshot.</summary>
internal sealed record DeliveryFile
{
    public string SourcePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Partition { get; init; } = "";
    public string SourceGroup { get; init; } = "";
    public bool Optional { get; init; }
    public bool Default { get; init; }
    public bool Generated { get; init; }
    public long? SourceLength { get; init; }
    public bool IncludeInSuper { get; init; }
    public bool Fastbootd { get; init; }
    /// <summary><para>自动添加的零大小分区条目（VAB B 槽伴随），不可编辑且豁免空镜像检查。</para>Auto-added zero-size partition (VAB B-slot companion); read-only and exempt from the empty-image check.</summary>
    public bool AutoZero { get; init; }
}

/// <summary>Payload options and selected signing identity; legacy unsupported inputs remain explicit.</summary>
internal sealed record DeliveryPayloadOptions
{
    public uint Version { get; init; } = 2;
    public uint MinorVersion { get; init; }
    public string DynamicPartitionsFile { get; init; } = "";
    public string SignatureFile { get; init; } = "";
    public string SigningKeyPath { get; init; } = "";
    public string SigningKeyFingerprint { get; init; } = "";
    public uint BlockSize { get; init; } = 4096;
    public long MaxTimestamp { get; init; }
    public string SecurityPatchLevel { get; init; } = "";
    public string Compression { get; init; } = "None";
    public string PostinstallFile { get; init; } = "";
}

/// <summary>Explicit single-device LP layout; sizes are bytes, never sparse file lengths.</summary>
internal sealed record DeliverySuperOptions
{
    public ulong DeviceSize { get; init; } = 9_126_805_504;
    public uint MetadataSize { get; init; } = 65536;
    public uint Slots { get; init; } = 3;
    public uint BlockSize { get; init; } = 4096;
    public string Group { get; init; } = "qti_dynamic_partitions";
    public string DeviceName { get; init; } = "super";
    public bool Sparse { get; init; }
    public bool VirtualAb { get; init; }
    public string DeviceType { get; init; } = "";
    public bool AutoZero { get; init; }
    public bool Empty { get; init; }
    public bool Zstd { get; init; }
    public List<DeliveryGroup> Groups { get; init; } = [];
}

/// <summary>A complete output snapshot, separate from mutable controls and project globals.</summary>
internal sealed record DeliveryPlan
{
    internal bool IsSuperEmptyOnly => PackSuper && Super.Empty;

    /// <summary>Projects metadata-only output without changing the saved image selections or script drafts.</summary>
    internal DeliveryPlan ForOutput() => !IsSuperEmptyOnly ? this : this with
    {
        OrganizeDirectory = false,
        Files = [],
        Container = Container.Select(file => file with { SourcePath = "", SourceLength = null }).ToList(),
        Txt = false,
        Windows = false,
        Linux = false,
        MacOS = false,
        Recovery = false,
        Scripts = [],
        Payload = new(),
        Package = new(),
        DeviceCodename = "",
        Slot = ""
    };

    public int Version { get; init; } = 1;
    public string OutputDirectory { get; init; } = "";
    public bool OrganizeDirectory { get; init; } = true;
    public bool PackSuper { get; init; }
    public bool PackPayload { get; init; }
    public DeliveryPayloadOptions Payload { get; init; } = new();
    public DeliveryPackageOptions Package { get; init; } = new();
    public bool Recovery { get; init; }
    public bool ContainerConfirmed { get; init; }
    public DeliverySuperOptions Super { get; init; } = new();
    public List<DeliveryFile> Container { get; init; } = [];
    public List<DeliveryFile> Files { get; init; } = [];
    public bool Txt { get; init; } = true;
    public bool TxtVariants { get; init; }
    public bool Windows { get; init; }
    public bool Linux { get; init; }
    public bool MacOS { get; init; }
    public string DeviceCodename { get; init; } = "";
    public string Information { get; init; } = "";
    public bool AskWipe { get; init; }
    public bool Reboot { get; init; }
    public string Slot { get; init; } = "";
    public Dictionary<string, string> Scripts { get; init; } = [];
    public string ScriptFingerprint { get; init; } = "";
    public Dictionary<string, string> GeneratedScripts { get; init; } = [];
    public List<string> OpenScripts { get; init; } = [];
    public Dictionary<string, int> ScriptCarets { get; init; } = [];
    public Dictionary<string, string> LocalZstd { get; init; } = [];
}

/// <summary>Bounded AOT-safe plan persistence through the solution file state section; paths are stored relative to the solution root.</summary>
internal static class DeliveryPlanStore
{
    /// <summary>Well-known key of the delivery plan inside the solution file's state section.</summary>
    internal const string StateKey = "delivery";

    /// <summary>
    /// <para>把交付计划序列化为解决方案状态段中的 JSON 元素；路径转相对解决方案根，超过 8 MiB 抛错。</para>
    /// Serializes the delivery plan into a JSON element for the solution state section; paths become
    /// relative to the solution root, and sizes beyond 8 MiB are rejected.
    /// </summary>
    internal static JsonElement CaptureElement(string root, DeliveryPlan plan)
    {
        var stored = Rebase(plan, path => Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path);
        var element = JsonSerializer.SerializeToElement(stored, DeliveryJsonContext.Default.DeliveryPlan);
        if (element.ValueKind is not JsonValueKind.Object || element.GetRawText().Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Delivery plan exceeds 8 MiB.");
        return element;
    }

    /// <summary>
    /// <para>从解决方案状态段还原交付计划；空值返回 null，非法结构抛错，路径转绝对。</para>
    /// Restores the delivery plan from the solution state section; null/empty values return null,
    /// invalid structures throw, and paths become absolute.
    /// </summary>
    internal static DeliveryPlan? LoadFromState(JsonElement? state, string root)
    {
        if (state is not { } element || element.ValueKind is not (JsonValueKind.Object or JsonValueKind.String)) return null;
        if (element.GetRawText().Length > 8 * 1024 * 1024) throw new InvalidDataException("Delivery plan exceeds 8 MiB.");
        var plan = element.Deserialize(DeliveryJsonContext.Default.DeliveryPlan);
        if (plan is null || plan.Version != 1 || plan.Files is null || plan.Container is null || plan.Super is null
            || plan.Package is null || plan.Package.CertificatePath is null || plan.Package.SigningKeyPath is null || plan.Package.BuildFingerprint is null || plan.Package.Incremental is null || plan.Package.RecoveryUpdaterPath is null
            || plan.Payload is null || plan.Payload.DynamicPartitionsFile is null || plan.Payload.SignatureFile is null || plan.Payload.PostinstallFile is null
            || plan.Payload.SecurityPatchLevel is null || plan.Payload.Compression is null
            || plan.Payload.SigningKeyPath is null || plan.Payload.SigningKeyFingerprint is null
            || plan.Scripts is null || plan.LocalZstd is null || plan.OutputDirectory is null || plan.DeviceCodename is null
            || plan.GeneratedScripts is null || plan.OpenScripts is null || plan.ScriptCarets is null
            || plan.GeneratedScripts.Any(p => p.Value is null) || plan.OpenScripts.Any(p => p is null)
            || plan.Information is null || plan.Slot is null || plan.ScriptFingerprint is null
            || plan.Super.Groups is null || plan.Super.Groups.Any(g => g is null || g.Name is null || g.Partitions is null || g.Partitions.Any(p => p is null))
            || plan.Super.DeviceName is null || plan.Super.Group is null || plan.Super.DeviceType is null
            || plan.Scripts.Any(p => p.Value is null) || plan.LocalZstd.Any(p => p.Value is null)
            || plan.Files.Concat(plan.Container).Any(f => f is null || f.SourcePath is null || f.FileName is null || f.Partition is null || f.SourceGroup is null))
            throw new InvalidDataException("Invalid delivery plan.");
        if (plan.PackPayload || plan.PackSuper)
            plan = plan with
            {
                Files = plan.Files.Select(f => f.Generated && (f.Partition == "payload" || f.Partition == "super")
                ? f with { FileName = DeliveryOutputService.ContainerFileName(plan) } : f).ToList()
            };
        return Rebase(plan, path => Path.GetFullPath(path, root));
    }

    private static DeliveryPlan Rebase(DeliveryPlan plan, Func<string, string> path) => plan with
    {
        OutputDirectory = string.IsNullOrWhiteSpace(plan.OutputDirectory) ? "" : path(plan.OutputDirectory),
        Package = plan.Package with { CertificatePath = string.IsNullOrEmpty(plan.Package.CertificatePath) ? "" : path(plan.Package.CertificatePath), SigningKeyPath = string.IsNullOrEmpty(plan.Package.SigningKeyPath) ? "" : path(plan.Package.SigningKeyPath), RecoveryUpdaterPath = string.IsNullOrEmpty(plan.Package.RecoveryUpdaterPath) ? "" : path(plan.Package.RecoveryUpdaterPath) },
        Payload = plan.Payload with
        {
            DynamicPartitionsFile = string.IsNullOrEmpty(plan.Payload.DynamicPartitionsFile) ? "" : path(plan.Payload.DynamicPartitionsFile),
            SignatureFile = string.IsNullOrEmpty(plan.Payload.SignatureFile) ? "" : path(plan.Payload.SignatureFile),
            SigningKeyPath = string.IsNullOrEmpty(plan.Payload.SigningKeyPath) ? "" : path(plan.Payload.SigningKeyPath),
            PostinstallFile = string.IsNullOrEmpty(plan.Payload.PostinstallFile) ? "" : path(plan.Payload.PostinstallFile)
        },
        LocalZstd = plan.LocalZstd.ToDictionary(p => p.Key, p => string.IsNullOrEmpty(p.Value) ? "" : path(p.Value)),
        Container = plan.Container.Select(f => f with { SourcePath = string.IsNullOrEmpty(f.SourcePath) ? "" : path(f.SourcePath) }).ToList(),
        Files = plan.Files.Select(f => f with { SourcePath = string.IsNullOrEmpty(f.SourcePath) ? "" : path(f.SourcePath) }).ToList()
    };
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DeliveryPlan))]
internal partial class DeliveryJsonContext : JsonSerializerContext;