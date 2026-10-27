using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Guards the single-authority rule for codecs and archive structure: third-party codec libraries may only be
/// imported inside <c>UotanStudio.Compress</c>, hash mixing may only live in <c>UotanStudio.Hash</c>, the sparse
/// magic may only be spelled by <c>SparseFormat</c>, and only the ZIP authority may write an end-of-central-directory
/// record.
/// <para>守护编解码器与归档结构的单一权威规则：第三方编解码库只允许在 <c>UotanStudio.Compress</c> 内引用，哈希混合
/// 只能位于 <c>UotanStudio.Hash</c>，稀疏魔数只能由 <c>SparseFormat</c> 书写，且只有 ZIP 权威实现可以写出中央目录
/// 结束记录。</para>
/// </summary>
public sealed class CodecAuthorityGuardTests
{
    private const string CodecLibraryDirectory = "UotanStudio.Compress";
    private const string HashDirectory = "UotanStudio.Hash";

    private static readonly IReadOnlyDictionary<string, string> RetainedCodecLibraryImports = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UotanStudio.Firmware/Archive/Browsing/ArchiveBrowser.cs"] =
            "The archive browser enumerates arbitrary third-party archives (7z, RAR, TAR) through SharpCompress readers, which the generic compression facade does not model.",
        ["UotanStudio.Firmware/OtaDelta/Diff/BinaryDeltaPatch.cs"] =
            "The bsdiff applier reads the bzip2 section of a third-party patch stream in place; the container framing is the patch format's own contract.",
    };

    private static readonly IReadOnlyDictionary<string, string> RetainedEndRecordWriters = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UotanStudio.Delivery/Build/Package.cs"] =
            "OTA finalization needs a ZIP64-aware stored-range reader and an in-place CRC repair, neither of which a streaming archive reader exposes.",
        ["UotanStudio.Firmware/AndroidMeta/Apk/ApkEntryStripper.cs"] =
            "APK member surgery rewrites stored members in place and cannot re-encode the archive.",
        ["UotanStudio.Firmware/AndroidMeta/Apk/ApkRepacker.cs"] =
            "APK repacking keeps original member offsets, so it works on the end record directly.",
        ["UotanStudio/Services/Firmware/RemoteRomService.cs"] =
            "Header sniffing over a remote HTTP response only inspects the end record signature.",
        ["UotanStudio.Projects/ProjectPackageIndex.cs"] =
            "Project package indexing only sniffs the end record signature.",
    };

    /// <summary>
    /// No production source outside the compression library imports a codec package directly.
    /// <para>压缩库之外的生产源码不得直接引用编解码包。</para>
    /// </summary>
    [Fact]
    public void ProductionSources_DoNotImportCodecLibrariesOutsideCompress()
    {
        var offenders = Scan(
            line => line.Contains("using ZstdSharp", StringComparison.Ordinal)
                || line.Contains("using K4os.", StringComparison.Ordinal)
                || line.Contains("using SharpCompress", StringComparison.Ordinal)
                || line.Contains("using SevenZip", StringComparison.Ordinal),
            RetainedCodecLibraryImports,
            CodecLibraryDirectory);

        Assert.True(offenders.Count == 0,
            "Production code imports a codec library outside UotanStudio.Compress: " + string.Join(", ", offenders)
            + ". Route the codec through UotanStudio.Compress and record any genuine format-domain exception with its reason.");
    }

    /// <summary>
    /// No production source outside the hash library carries its own FNV mixing.
    /// <para>哈希库之外的生产源码不得自带 FNV 混合实现。</para>
    /// </summary>
    [Fact]
    public void ProductionSources_DoNotCarryAnFnvHashOutsideTheHashLibrary()
    {
        var offenders = Scan(
            line => line.Contains("1099511628211", StringComparison.Ordinal)
                || line.Contains("14695981039346656037", StringComparison.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            HashDirectory);

        Assert.True(offenders.Count == 0,
            "Production code carries an FNV-1a implementation outside UotanStudio.Hash: " + string.Join(", ", offenders)
            + ". Move the mixing into UotanStudio.Hash.Fnv1a.Fnv1a64 instead of re-deriving it.");
    }

    /// <summary>
    /// No production source outside the sparse reader spells the Android sparse magic literal.
    /// <para>稀疏读取器之外的生产源码不得书写 Android 稀疏魔数字面量。</para>
    /// </summary>
    [Fact]
    public void ProductionSources_DoNotRedefineTheSparseMagic()
    {
        var offenders = Scan(
            line => line.Contains("0xED26FF3A", StringComparison.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            "UotanStudio.Firmware/Core/BlockImageMagic.cs");

        Assert.True(offenders.Count == 0,
            "Production code spells the sparse magic outside SparseFormat.SparseHeaderMagic: " + string.Join(", ", offenders)
            + ". Reference the constant so a format change cannot drift between readers.");
    }

    /// <summary>
    /// Only the ZIP authority and the recorded in-place writers locate or emit an end-of-central-directory record.
    /// <para>只有 ZIP 权威实现与已记录的原地写入者可以定位或写出中央目录结束记录。</para>
    /// </summary>
    [Fact]
    public void ProductionSources_OnlyTheZipAuthorityWritesEndRecords()
    {
        var offenders = Scan(
            line => line.Contains("0x06054b50", StringComparison.OrdinalIgnoreCase),
            RetainedEndRecordWriters,
            "UotanStudio.Compress/Zip/");

        Assert.True(offenders.Count == 0,
            "Production code writes or locates a ZIP end record outside UotanStudio.Compress.Zip: " + string.Join(", ", offenders)
            + ". Use ZipContainer.AppendArchiveComment or ZipContainer.ValidateSingleDiskLayout.");
    }

    private static List<string> Scan(Func<string, bool> matches, IReadOnlyDictionary<string, string> retained, string allowedDirectory)
    {
        var repositoryRoot = LocateRepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in EnumerateProductionSources(repositoryRoot))
        {
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            if (relative.StartsWith(allowedDirectory, StringComparison.Ordinal) || retained.ContainsKey(relative))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (matches(line))
                {
                    offenders.Add(relative);
                    break;
                }
            }
        }

        return offenders;
    }

    private static IEnumerable<string> EnumerateProductionSources(string repositoryRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(repositoryRoot, "UotanStudio*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "UotanStudio.Tests", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(segment => segment is "bin" or "obj" or "artifacts" or ".git" or "reference"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UotanStudio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The checked-out UotanStudio.slnx could not be located.");
    }
}