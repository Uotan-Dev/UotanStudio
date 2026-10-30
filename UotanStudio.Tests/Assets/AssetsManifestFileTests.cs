using UotanStudio.Assets;
using UotanStudio.Compress;
using UotanStudio.Hash.Digests;
using UotanStudio.Tests.Architecture;
using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Assets;

/// <summary>
/// Checks the checked-in manifest against the frames on disk: the declared rows and the <c>*.zst</c> files under
/// <c>UotanStudio.Assets/Assets</c> form a bijection, and every frame decodes to the length and SHA-256 its row records.
/// <para>校验入库清单与实际帧：声明的行与 <c>UotanStudio.Assets/Assets</c> 下的 <c>*.zst</c> 文件互为双射，
/// 且每个帧解出的长度与 SHA-256 与其清单行一致。</para>
/// </summary>
public class AssetsManifestFileTests
{
    private const string AssetsPath = "UotanStudio.Assets/Assets";

    private const string ManifestPath = "UotanStudio.Assets/Assets/manifest.tsv";

    [Fact]
    public void Manifest_DeclaredRowsAndFramesOnDisk_AreInBijection()
    {
        var declared = DeclaredEntries()
            .Select(static entry => $"{entry.Bundle}/{entry.Path}.zst")
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var onDisk = FramesOnDisk();

        Assert.Equal(declared, onDisk);
    }

    [Fact]
    public void Manifest_EveryDeclaredRow_DecodesToDeclaredLengthAndDigest()
    {
        var assetsDirectory = Path.Combine(RepositoryRoot(), AssetsPath.Replace('/', Path.DirectorySeparatorChar));
        var failures = new List<string>();
        foreach (var entry in DeclaredEntries())
        {
            var framePath = Path.Combine(
                assetsDirectory,
                entry.Bundle.Replace('/', Path.DirectorySeparatorChar),
                entry.Path + ".zst");
            if (!File.Exists(framePath))
            {
                failures.Add($"{entry.Bundle}/{entry.Path}: no frame at '{framePath}'. 清单行 '{entry.Bundle}/{entry.Path}' 在 '{framePath}' 没有对应帧。");
                continue;
            }

            var payload = CompressionService.Decompress(File.ReadAllBytes(framePath), CompressionFormat.Zstd);
            if (payload.LongLength != entry.Length)
            {
                failures.Add(
                    $"{entry.Bundle}/{entry.Path}: {entry.Length} bytes declared, {payload.LongLength} decoded. "
                    + $"清单声明 {entry.Length} 字节，实际解出 {payload.LongLength} 字节。");
                continue;
            }

            var digest = Sha256.ComputeHexLower(payload);
            if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{entry.Bundle}/{entry.Path}: {entry.Sha256} recorded, {digest} decoded. "
                    + $"清单记录 {entry.Sha256}，实际 {digest}。");
            }
        }

        Assert.True(failures.Count == 0, "Manifest rows must match their frames:\n" + string.Join("\n", failures));
    }

    private static AssetEntry[] DeclaredEntries()
    {
        var manifestPath = Path.Combine(RepositoryRoot(), ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(
            File.Exists(manifestPath),
            $"The repository manifest '{ManifestPath}' is missing. 仓库清单 '{ManifestPath}' 缺失。");

        var bundles = AssetManifest.Parse(File.ReadAllBytes(manifestPath));
        return [.. bundles.Values.SelectMany(static bundle => bundle.Entries)];
    }

    private static string[] FramesOnDisk()
    {
        var assetsDirectory = Path.Combine(RepositoryRoot(), AssetsPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(
            Directory.Exists(assetsDirectory),
            $"The asset directory '{AssetsPath}' is missing. 资源目录 '{AssetsPath}' 缺失。");

        var frames = Directory.EnumerateFiles(assetsDirectory, "*.zst", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(assetsDirectory, path).Replace('\\', '/'))
            .OrderBy(static name => name, StringComparer.Ordinal);
        return [.. frames];
    }

    private static string RepositoryRoot()
    {
        var found = FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var located);
        Assert.SkipWhen(
            !found || located is null,
            "The test host is not running inside a UotanStudio.slnx checkout, so UotanStudio.Assets/Assets/manifest.tsv is not on disk.");

        return RepositoryLayout.LocateRoot();
    }
}