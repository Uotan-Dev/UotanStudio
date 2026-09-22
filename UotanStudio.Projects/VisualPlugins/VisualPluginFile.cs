using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>AOT-safe and bounded persistence for portable visual-plugin JSON files.</summary>
public static class VisualPluginFile
{
    /// <summary>Synchronously loads one small startup package with the same bounds and validation as the async path.</summary>
    public static VisualPluginPackage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsBundle(path))
        {
            return VisualPluginBundle.Load(path);
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.SequentialScan);
        EnsureSize(stream.Length);
        VisualPluginPackage package;
        try
        {
            package = JsonSerializer.Deserialize(stream, VisualPluginJsonContext.Default.VisualPluginPackage)
                ?? throw new InvalidDataException("Visual-plugin package is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Visual-plugin package has an invalid or unsupported document structure.", error);
        }
        EnsureValid(package);
        return package;
    }

    /// <summary>Saves a structurally valid package through the existing atomic JSON publication path.</summary>
    public static async Task SaveAsync(string path, VisualPluginPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(package);
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));

        if (path.EndsWith(".uotanplugin", StringComparison.OrdinalIgnoreCase))
        {
            await VisualPluginBundle.SaveAsync(path, package, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0))
        {
            throw new InvalidDataException("Packages with resources must be saved as .uotanplugin bundles.");
        }

        await AtomicJsonFile.WriteIfChangedAsync(path, package,
            VisualPluginJsonContext.Default.VisualPluginPackage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads and validates one untrusted package without reading more than the package limit.</summary>
    public static async Task<VisualPluginPackage> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsBundle(path))
        {
            var bundle = VisualPluginBundle.Load(path);
            cancellationToken.ThrowIfCancellationRequested();
            return bundle;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureSize(stream.Length);

        VisualPluginPackage package;
        try
        {
            package = await JsonSerializer.DeserializeAsync(stream,
                VisualPluginJsonContext.Default.VisualPluginPackage, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Visual-plugin package is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Visual-plugin package has an invalid or unsupported document structure.", error);
        }
        EnsureValid(package);
        return package;
    }

    private static void EnsureSize(long length)
    {
        if (length > VisualPluginValidation.MaximumFileSize)
            throw new InvalidDataException($"Visual-plugin package exceeds {VisualPluginValidation.MaximumFileSize} bytes.");
    }

    private static void EnsureValid(VisualPluginPackage package)
    {
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        if (VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0))
        {
            throw new InvalidDataException("JSON packages cannot provide bundled resources.");
        }
    }

    private static bool IsBundle(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == signature.Length && signature.SequenceEqual("PK\u0003\u0004"u8);
    }
}

[JsonSerializable(typeof(VisualPluginPackage))]
[JsonSerializable(typeof(VisualPluginManifest))]
[JsonSerializable(typeof(VisualPluginPortDefinition))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class VisualPluginJsonContext : JsonSerializerContext;