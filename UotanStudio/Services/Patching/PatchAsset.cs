using UotanStudio.Firmware.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Patching;

/// <summary>Immutable resource identity and private source snapshot passed between patch nodes.<para>在补丁节点间传递的不可变资源身份与私有来源快照。</para></summary>
public sealed class PatchAsset
{
    private readonly VisualPluginPackage _source;

    private PatchAsset(VisualPluginPackage source, VisualPluginAsset asset)
    {
        _source = source;
        Id = asset.Id;
        Name = asset.Name;
        Length = asset.Length;
        Sha256 = asset.Sha256;
    }

    /// <summary>Package-local resource identifier.<para>包内资源标识。</para></summary>
    /// <value>Identifier of the referenced asset.<para>所引用资源的标识。</para></value>
    public string Id { get; }

    /// <summary>Display name, never used as a destination path.<para>显示名称，不作为目标路径使用。</para></summary>
    /// <value>Human-readable asset name.<para>可读的资源名称。</para></value>
    public string Name { get; }

    /// <summary>Expected resource length.<para>预期资源长度。</para></summary>
    /// <value>Byte length the resource must have.<para>资源应具有的字节长度。</para></value>
    public long Length { get; }

    /// <summary>Expected SHA-256.<para>预期 SHA-256。</para></summary>
    /// <value>Lowercase hexadecimal digest.<para>小写十六进制摘要。</para></value>
    public string Sha256 { get; }

    /// <summary>Captures resource identity without exposing the source path or mutable package.<para>捕获资源身份，不暴露来源路径或可变包。</para></summary>
    /// <param name="package">Package providing the manifest and assets.<para>提供清单与资源的包。</para></param>
    /// <param name="id">Identifier of the asset to capture.<para>要捕获的资源标识。</para></param>
    /// <returns>Asset holding the verified resource identity.<para>持有已校验资源身份的资产。</para></returns>
    public static PatchAsset Resolve(VisualPluginPackage package, string id)
    {
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        if (package.BundleRoot is { } root)
        {
            var rootErrors = VisualPluginValidation.Validate(root);
            if (rootErrors.Count != 0)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, rootErrors));
            }
        }
        var source = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = package.Manifest.Id, Name = package.Manifest.Name },
            Assets = [.. package.Assets.Select(asset => asset.Snapshot())],
            BundlePath = package.BundlePath,
            BundleRoot = SnapshotResourceScope(package.BundleRoot ?? package)
        };
        var selected = source.Assets.SingleOrDefault(asset => string.Equals(asset.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Patch resource '{id}' does not exist.");
        return new PatchAsset(source, selected);
    }

    private static VisualPluginPackage SnapshotResourceScope(VisualPluginPackage package)
    {
        var snapshot = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = package.Manifest.Id, Name = package.Manifest.Name },
            Assets = [.. package.Assets.Select(asset => asset.Snapshot())]
        };
        foreach (var node in package.Graph.Nodes.Where(node => node.CompositePackage is not null))
        {
            var child = SnapshotResourceScope(node.CompositePackage!);
            snapshot.Graph.Nodes.Add(new NodeDto
            {
                InstanceId = node.InstanceId,
                TypeKey = child.NodeTypeKey,
                CompositePackage = child
            });
            snapshot.Manifest.RequiredNodeTypes.Add(child.NodeTypeKey);
        }
        return snapshot;
    }

    /// <summary>Verifies and copies resource bytes to a staging stream.<para>校验并把资源字节复制到暂存流。</para></summary>
    /// <param name="destination">Stream receiving the copied bytes.<para>接收复制字节的流。</para></param>
    /// <param name="token">Token cancelling the copy.<para>取消复制的令牌。</para></param>
    internal Task CopyToAsync(Stream destination, CancellationToken token)
        => VisualPluginBundle.CopyAssetAsync(_source, Id, destination, token);

    /// <summary>Verifies and strictly decodes a bounded configuration resource.<para>校验并严格解码有界的配置资源。</para></summary>
    /// <param name="token">Token cancelling the read.<para>取消读取的令牌。</para></param>
    /// <returns>Decoded text document of the resource.<para>该资源解码后的文本文档。</para></returns>
    public async Task<PatchTextDocument> ReadTextAsync(CancellationToken token)
    {
        if (Length > TextPatch.MaxCharacters * 4L)
        {
            throw new InvalidDataException("Configuration resource exceeds the byte limit.");
        }
        using var buffer = new MemoryStream((int)Length);
        await CopyToAsync(buffer, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return PatchFileService.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}