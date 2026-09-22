using UotanStudio.Firmware.BoundedContexts.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Patching;

/// <summary>Immutable resource identity and private source snapshot passed between patch nodes.</summary>
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

    /// <summary>Package-local resource identifier.</summary>
    public string Id { get; }

    /// <summary>Display name, never used as a destination path.</summary>
    public string Name { get; }

    /// <summary>Expected resource length.</summary>
    public long Length { get; }

    /// <summary>Expected SHA-256.</summary>
    public string Sha256 { get; }

    /// <summary>Captures resource identity without exposing the source path or mutable package.</summary>
    public static PatchAsset Resolve(VisualPluginPackage package, string id)
    {
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        var source = new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = package.Manifest.Id, Name = package.Manifest.Name },
            Assets = package.Assets.Select(asset => asset.Snapshot()).ToList(),
            BundlePath = package.BundlePath
        };
        var selected = source.Assets.SingleOrDefault(asset => string.Equals(asset.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Patch resource '{id}' does not exist.");
        return new PatchAsset(source, selected);
    }

    /// <summary>Verifies and copies resource bytes to a caller-owned staging stream.</summary>
    internal Task CopyToAsync(Stream destination, CancellationToken token)
        => VisualPluginBundle.CopyAssetAsync(_source, Id, destination, token);

    /// <summary>Verifies and strictly decodes a bounded configuration resource.</summary>
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