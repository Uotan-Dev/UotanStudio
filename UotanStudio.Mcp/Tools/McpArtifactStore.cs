using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Stores large MCP artifacts inside the sandbox and returns small references.
/// <para>在沙箱内存储大型 MCP 工件并返回小型引用。</para>
/// </summary>
internal sealed class McpArtifactStore
{
    /// <summary>Threshold above which payloads should be externalized. <para>超过该阈值的负载应外部化。</para></summary>
    internal const int DefaultThresholdBytes = 32 * 1024;

    private const string StoreDirectoryName = ".uotanstudio-mcp";
    private const string ArtifactsDirectoryName = "artifacts";
    private const string IndexFileName = "index.json";
    private static readonly JsonTypeInfo<List<ArtifactRef>> IndexTypeInfo =
        (JsonTypeInfo<List<ArtifactRef>>)ToolJson.SerializerOptions.GetTypeInfo(typeof(List<ArtifactRef>));

    private readonly PathSandbox _sandbox;
    private readonly string _root;
    private readonly string _indexPath;
    private readonly Lock _gate = new();

    public McpArtifactStore(PathSandbox sandbox)
    {
        _sandbox = sandbox;
        var baseRoot = sandbox.Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "mcp-sandbox");
        _root = Path.Combine(baseRoot, StoreDirectoryName, ArtifactsDirectoryName);
        _indexPath = Path.Combine(_root, IndexFileName);
        Directory.CreateDirectory(_root);
    }

    internal string Root => _root;

    internal ArtifactRef CreateTextArtifact(string kind, string content, string? extension = ".txt", string? preview = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        content ??= string.Empty;
        var bytes = Encoding.UTF8.GetBytes(content);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = $"{kind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..Math.Min(80, kind.Length + 33)];
        var safeExt = string.IsNullOrWhiteSpace(extension) ? ".txt" : extension.StartsWith('.') ? extension : "." + extension;
        var fileName = id + safeExt;
        var fullPath = Path.Combine(_root, fileName);
        File.WriteAllBytes(fullPath, bytes);
        var displayPath = ToDisplayPath(fullPath);
        var artifact = new ArtifactRef(id, displayPath, bytes.LongLength, sha, preview ?? CreatePreview(content));
        UpsertIndex(artifact);
        return artifact;
    }

    internal ArtifactRef CreateJsonArtifact<T>(string kind, T value, string? preview = null)
    {
        var json = JsonSerializer.Serialize(value, ToolJson.TypeInfo<T>());
        return CreateTextArtifact(kind, json, ".json", preview ?? CreatePreview(json));
    }

    internal ArtifactRef ReferenceExistingFile(string kind, string fullPath, string? preview = null)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = $"{kind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..Math.Min(80, kind.Length + 33)];
        var displayPath = ToDisplayPath(fullPath);
        var p = preview ?? CreatePreview(TryReadTextPreview(fullPath));
        var artifact = new ArtifactRef(id, displayPath, bytes.LongLength, sha, p);
        UpsertIndex(artifact);
        return artifact;
    }

    internal bool TryRead(string id, out ArtifactRef? artifact, out string? fullPath)
    {
        lock (_gate)
        {
            var items = ReadIndex();
            artifact = items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (artifact is null)
            {
                fullPath = null;
                return false;
            }

            fullPath = ResolveArtifactPath(artifact.Path);
            return true;
        }
    }

    internal IReadOnlyList<ArtifactRef> List() { lock (_gate) return ReadIndex(); }

    private string ResolveArtifactPath(string displayPath)
    {
        if (Path.IsPathRooted(displayPath)) return Path.GetFullPath(displayPath);
        var root = _sandbox.Root ?? Path.GetDirectoryName(_root) ?? _root;
        return Path.GetFullPath(Path.Combine(root, displayPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private string ToDisplayPath(string fullPath)
    {
        var root = _sandbox.Root;
        if (root is null) return fullPath;
        var rel = Path.GetRelativePath(root, fullPath);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return fullPath;
        return rel.Replace('\\', '/');
    }

    private static string CreatePreview(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    private static string TryReadTextPreview(string fullPath)
    {
        try
        {
            using var fs = File.OpenRead(fullPath);
            var len = (int)Math.Min(4096, fs.Length);
            var buf = new byte[len];
            var read = fs.Read(buf, 0, len);
            return Encoding.UTF8.GetString(buf, 0, read);
        }
        catch { return string.Empty; }
    }

    private List<ArtifactRef> ReadIndex()
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_indexPath);
            return JsonSerializer.Deserialize(json, IndexTypeInfo) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private void UpsertIndex(ArtifactRef artifact)
    {
        lock (_gate)
        {
            var items = ReadIndex();
            var idx = items.FindIndex(i => string.Equals(i.Id, artifact.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                items[idx] = artifact;
            }
            else
            {
                items.Add(artifact);
            }

            var json = JsonSerializer.Serialize(items, IndexTypeInfo);
            var tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _indexPath, overwrite: true);
        }
    }
}