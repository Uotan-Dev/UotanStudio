using UotanStudio.Firmware.BootSecurity.Patcher;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Sweeps the checked-out root patch package corpus so the Magisk payload contract and the recognized
/// unsupported layouts stay pinned to real upstream releases.
/// <para>遍历仓库内的根补丁包语料，使 Magisk 载荷契约与已识别的不支持布局始终对齐真实上游发布版。</para>
/// </summary>
public sealed class RootPatchPackageCorpusTests
{
    private const string CorpusVariable = "UOTAN_PATCH_PKG_CORPUS";

    [Fact]
    public async Task Corpus_MagiskApks_AreSupportedWithAnInstallablePayload()
    {
        var directory = CorpusDirectory();
        var packages = Directory.GetFiles(directory, "*.apk", SearchOption.AllDirectories)
            .Where(path => Family(path).Equals("magisk", StringComparison.OrdinalIgnoreCase)
                || Family(path).Equals("magisk-alpha", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(packages);

        var failures = new List<string>();
        foreach (var package in packages)
        {
            var detection = await BootPatchAnalyzer.AnalyzeAsync(package, TestContext.Current.CancellationToken);
            if (detection.Kind != BootPatchKind.Magisk || !detection.IsSupported)
                failures.Add($"{Path.GetFileName(package)}: kind={detection.Kind} supported={detection.IsSupported} reason={detection.UnsupportedReason}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Corpus_LegacyMagiskAndManagerOnlySukiSu_ReportTheirUnsupportedReason()
    {
        var directory = CorpusDirectory();
        var legacy = Directory.GetFiles(Path.Combine(directory, "magisk"), "Magisk-v*.zip", SearchOption.AllDirectories);
        var managerOnly = Directory.GetFiles(Path.Combine(directory, "sukisu"), "SukiSU_v3.1*.apk", SearchOption.AllDirectories);
        Assert.NotEmpty(legacy);
        Assert.NotEmpty(managerOnly);

        var failures = new List<string>();
        foreach (var package in legacy.Concat(managerOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            var detection = await BootPatchAnalyzer.AnalyzeAsync(package, TestContext.Current.CancellationToken);
            if (detection.IsSupported || string.IsNullOrWhiteSpace(detection.UnsupportedReason))
                failures.Add($"{Path.GetFileName(package)}: kind={detection.Kind} supported={detection.IsSupported} reason={detection.UnsupportedReason}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Corpus_KernelSuLkmPatch_StagesKsuinitAndAVerifiedModule()
    {
        var directory = CorpusDirectory();
        var package = Directory.GetFiles(Path.Combine(directory, "kernelsu"), "KernelSU_v*.apk",
                SearchOption.AllDirectories)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .First();
        var cancellationToken = TestContext.Current.CancellationToken;
        var detection = await BootPatchAnalyzer.AnalyzeAsync(package, cancellationToken);
        Assert.Equal(BootPatchKind.KernelSu, detection.Kind);
        Assert.NotEmpty(detection.Kmis!);

        var work = Path.Combine(Path.GetTempPath(), "uotan-corpus-ksu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = new byte[0x2000];
            "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
            System.Text.Encoding.UTF8
                .GetBytes($"\0Linux version 5.15.123-{detection.Kmis![0]} (builder)\0")
                .CopyTo(kernel.AsSpan(0x200));
            var bootPath = Path.Combine(work, "boot.img");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, BuildCpio(("init", "stock-init"u8.ToArray()))));

            var result = await new BootPatchService().PatchAsync(package, bootPath, outputPath,
                new BootPatchOptions { Mode = BootPatchMode.Lkm }, cancellationToken);

            Assert.Contains(result.Changes, change => change.Contains("ksuinit", StringComparison.Ordinal));
            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            var archive = new UotanStudio.Firmware.Archive.CpioArchive();
            using var ramdisk = new MemoryStream(parser.Ramdisk);
            var names = archive.ReadAll(ramdisk).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("ksuinit", names);
            Assert.Contains("init.real", names);
            Assert.Contains("kernelsu.ko", names);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static byte[] BuildCpio(params (string Name, byte[] Data)[] entries)
    {
        var archive = new UotanStudio.Firmware.Archive.CpioArchive();
        using var stream = new MemoryStream();
        archive.SaveAll(stream, [.. entries.Select(entry => new UotanStudio.Firmware.Archive.Models.ArchiveEntry
        {
            Name = entry.Name,
            Data = entry.Data,
            Metadata = new UotanStudio.Firmware.Archive.Models.ArchiveEntryMetadata
            {
                FileType = UotanStudio.Firmware.Archive.Models.CpioFileType.Regular,
                UnixPermissions = 0x81ED,
                LinkCount = 1
            }
        })], UotanStudio.Firmware.Archive.Models.ArchiveFormat.NewAscii);
        return stream.ToArray();
    }

    private static byte[] PackBootImage(byte[] kernel, byte[] ramdisk)
    {
        var packer = new UotanStudio.Firmware.BootSecurity.BootImage.BootImagePacker
        {
            Header = new UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageHeader
            {
                HeaderVersion = UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3,
                HeaderSize = 1580,
                OsVersion = 0x01030000,
                CommandLine = "androidboot.slot_suffix=_a",
            },
            Kernel = kernel,
            Ramdisk = ramdisk,
        };
        using var stream = new MemoryStream();
        packer.PackToStream(stream);
        return stream.ToArray();
    }

    private static string CorpusDirectory()
    {
        var directory = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            $"Set {CorpusVariable} to the reference/patch-pkg directory to run the root patch package corpus sweep.");
        return directory!;
    }

    private static string Family(string path)
    {
        var relative = Path.GetRelativePath(Environment.GetEnvironmentVariable(CorpusVariable)!, path);
        var separator = relative.IndexOfAny(['\\', '/']);
        return separator < 0 ? string.Empty : relative[..separator];
    }
}