using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// An unpacked SpacemiT flash package (titan flasher layout): the <c>fastboot.yaml</c>
/// script plus the partition table JSON files and images it references.
/// <para>解包后的 SpacemiT 烧录包（titan flasher 布局）：<c>fastboot.yaml</c> 脚本，
/// 以及它引用的分区表 JSON 与镜像文件。</para>
/// </summary>
public sealed class SpacemiTFlashPackage
{
    /// <summary>
    /// Name of the flash script file every package must contain at its root.
    /// <para>每个烧录包根目录必须包含的刷机脚本文件名。</para>
    /// </summary>
    public const string ScriptFileName = "fastboot.yaml";

    /// <summary>Upper bound on the file count a regex image match enumerates, so a huge or cyclic package cannot stall resolution. <para>正则镜像匹配枚举的文件数上限，避免过大或循环的包拖垮解析。</para></summary>
    private const int MaxPackageFiles = 4096;

    /// <summary>Upper bound on the length of a regex image reference, so an untrusted package cannot feed an oversized pattern to the regex engine. <para>正则镜像引用的长度上限，避免不可信包把超长模式送入正则引擎。</para></summary>
    private const int MaxRegexReferenceLength = 256;

    /// <summary>Wall-clock budget for one regex image match against a package-relative path. <para>对单个包根相对路径执行正则镜像匹配的墙上时间预算。</para></summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Gets the package root directory. <para>获取烧录包根目录。</para></summary>
    public string RootPath { get; }

    /// <summary>Gets the parsed flash script. <para>获取解析后的刷机脚本。</para></summary>
    public SpacemiTFlashScript Script { get; }

    private SpacemiTFlashPackage(string rootPath, SpacemiTFlashScript script)
    {
        RootPath = rootPath;
        Script = script;
    }

    /// <summary>
    /// Loads an unpacked flash package directory.
    /// <para>加载解包后的烧录包目录。</para>
    /// </summary>
    /// <param name="rootPath">Directory containing <c>fastboot.yaml</c>; the path is normalized to its full form. <para>包含 <c>fastboot.yaml</c> 的目录；路径会规范化为完整形式。</para></param>
    /// <returns>The loaded package. <para>加载后的烧录包。</para></returns>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist or the script file is missing. <para>目录不存在或脚本文件缺失。</para></exception>
    /// <exception cref="InvalidDataException">The script is not a valid SpacemiT flash script. <para>脚本不是有效的 SpacemiT 刷机脚本。</para></exception>
    public static SpacemiTFlashPackage Load(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var scriptPath = Path.Combine(root, ScriptFileName);
        if (!File.Exists(scriptPath))
        {
            throw new DirectoryNotFoundException($"Flash package root '{root}' has no {ScriptFileName}.");
        }

        return new SpacemiTFlashPackage(root, SpacemiTFlashScript.Load(scriptPath));
    }

    /// <summary>
    /// Resolves a package file reference ("factory/FSBL.bin" or a bare basename) to an existing
    /// file under the package root, rejecting paths that escape the root; a regex image reference
    /// resolves only when it matches exactly one package file.
    /// <para>把包内文件引用（"factory/FSBL.bin" 或裸文件名）解析为包根下的现有文件，拒绝逃出包根
    /// 的路径；正则镜像引用仅在恰好命中一个包内文件时解析。</para>
    /// </summary>
    /// <param name="reference">Reference text from the untrusted package. <para>来自不可信包文件的引用文本。</para></param>
    /// <param name="cancellationToken">Cancels regex image resolution while enumerating package files. <para>在枚举包内文件解析正则镜像期间取消。</para></param>
    /// <returns>The resolved file path, or <see langword="null"/> when the file does not exist or the reference is a regex image marker with zero or multiple matches. <para>解析出的文件路径；文件不存在，或引用为命中零个或多文件的正则镜像标记时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The reference is a regex image marker with an invalid or oversized pattern, or the package holds more files than the enumeration bound. <para>引用为模式非法或过长的正则镜像标记，或包内文件数超过枚举上限。</para></exception>
    public string? ResolveFile(string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (IsRegexReference(reference))
        {
            var matches = ResolveImageMatches(reference, cancellationToken);
            return matches.Count == 1 ? matches[0] : null;
        }

        foreach (var candidate in EnumerateCandidates(reference))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a package image reference to the files it names: a regex image reference
    /// (<c>/…/</c>) is matched against the package-root-relative path of every file in the package
    /// (slash-separated), while a plain reference resolves as a single file path.
    /// <para>把包内镜像引用解析为其命中的文件：正则镜像引用（<c>/…/</c>）对包内全部文件的包根相对
    /// 路径（统一 '/' 分隔符）做匹配；普通引用解析为单个文件路径。</para>
    /// </summary>
    /// <param name="reference">Reference text from the untrusted package. <para>来自不可信包文件的引用文本。</para></param>
    /// <param name="cancellationToken">Cancels the package file enumeration and regex matching. <para>取消包内文件枚举与正则匹配。</para></param>
    /// <returns>Every matching file path in package-root order, one entry for a resolved plain reference, or an empty list when nothing matches. <para>按包根顺序排列的全部命中文件路径；普通引用解析成功时为单元素列表；无命中时为空列表。</para></returns>
    /// <exception cref="InvalidDataException">The regex pattern is invalid, too long, or the package holds more files than the enumeration bound. <para>正则模式非法、过长，或包内文件数超过枚举上限。</para></exception>
    public IReadOnlyList<string> ResolveImageMatches(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return [];
        }

        if (!IsRegexReference(reference))
        {
            var resolved = ResolveFile(reference, cancellationToken);
            return resolved is null ? [] : [resolved];
        }

        if (reference.Length > MaxRegexReferenceLength)
        {
            throw new InvalidDataException($"Regex image reference exceeds the {MaxRegexReferenceLength}-character limit.");
        }

        Regex regex;
        try
        {
            regex = new Regex(reference[1..^1], RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"Image reference '{reference}' is not a valid regex.", e);
        }

        List<string>? matches = null;
        var enumerated = 0;
        foreach (var path in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerated++;
            if (enumerated > MaxPackageFiles)
            {
                throw new InvalidDataException(
                    $"Flash package '{RootPath}' holds more than {MaxPackageFiles} files; regex image resolution is bounded.");
            }

            var relative = Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
            if (regex.IsMatch(relative))
            {
                matches ??= [];
                matches.Add(path);
            }
        }

        if (matches is null)
        {
            return [];
        }

        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    /// <summary>
    /// Resolves a <c>partition_{size}.json</c> template answer ("2M", "universal") to the
    /// matching partition table file, falling back through halved "NM" sizes exactly like
    /// the vendor reference flow.
    /// <para>把 <c>partition_{size}.json</c> 模板应答（"2M"、"universal"）解析为匹配的分区表
    /// 文件；"NM" 尺寸按厂商参考流程的方式逐级减半回退。</para>
    /// </summary>
    /// <param name="sizeValue">Raw getvar answer (<see cref="SpacemiTConstants.VarMtdSize"/> or <see cref="SpacemiTConstants.VarBlockSize"/>); "NULL" or empty means the storage kind is absent. <para>getvar 原始应答（<see cref="SpacemiTConstants.VarMtdSize"/> 或 <see cref="SpacemiTConstants.VarBlockSize"/>）；"NULL" 或空表示该存储类型不存在。</para></param>
    /// <returns>The resolved table file path, or <see langword="null"/> when the storage kind is absent or no table matches. <para>解析出的分区表路径；存储类型不存在或无匹配表时为 <see langword="null"/>。</para></returns>
    public string? ResolvePartitionTable(string? sizeValue)
    {
        if (IsAbsentStorageAnswer(sizeValue))
        {
            return null;
        }

        var trimmed = sizeValue!.Trim();

        if (trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var name in PartitionFileNames(trimmed))
        {
            var resolved = ResolveFile(name);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports whether a <c>mtd-size</c>/<c>blk-size</c> answer means the storage kind is absent:
    /// the device answers <c>OKAY NULL</c> (or an empty value) instead of a size when the
    /// corresponding storage is not fitted, and the vendor flow silently skips that stage.
    /// <para>判断 <c>mtd-size</c>/<c>blk-size</c> 应答是否表示该存储类型不存在：未装配对应存储时
    /// 设备应答 <c>OKAY NULL</c>（或空值）而非尺寸，厂商流程会静默跳过该阶段。</para>
    /// </summary>
    /// <param name="sizeValue">Raw getvar answer. <para>getvar 原始应答。</para></param>
    /// <returns><see langword="true"/> when the storage kind is absent. <para>存储类型不存在时为 <see langword="true"/>。</para></returns>
    public static bool IsAbsentStorageAnswer(string? sizeValue)
    {
        var trimmed = sizeValue?.Trim() ?? string.Empty;
        return trimmed.Length == 0 || trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reports whether a package file reference is a vendor regex image marker (<c>/…/</c>) that names no concrete file. <para>判断包内文件引用是否为不指向具体文件的厂商正则镜像标记（<c>/…/</c>）。</para></summary>
    internal static bool IsRegexReference(string reference) =>
        reference.Length > 1 && reference[0] == '/' && reference[^1] == '/';

    private static IEnumerable<string> PartitionFileNames(string sizeValue)
    {
        var exact = SpacemiTConstants.PartitionFileTemplate.Replace("{0}", sizeValue, StringComparison.Ordinal);
        yield return exact;

        var match = System.Text.RegularExpressions.Regex.Match(sizeValue, "^(\\d+)[Mm]$");
        if (!match.Success)
        {
            yield break;
        }

        var sizeMb = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 2;
        while (sizeMb >= 1)
        {
            yield return SpacemiTConstants.PartitionFileTemplate
                .Replace("{0}", $"{sizeMb}M", StringComparison.Ordinal);
            sizeMb /= 2;
        }
    }

    private IEnumerable<string> EnumerateCandidates(string reference)
    {
        var relative = reference.Trim();
        if (relative.Length == 0 || relative.Length > 260)
        {
            yield break;
        }

        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
        {
            yield break;
        }

        var direct = Path.GetFullPath(Path.Combine(RootPath, relative));
        if (direct.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            direct.StartsWith(RootPath + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            yield return direct;
        }

        var basename = Path.GetFileName(relative);
        if (basename.Length == 0 || basename == relative)
        {
            yield break;
        }

        var flat = Path.GetFullPath(Path.Combine(RootPath, basename));
        Debug.Assert(flat.StartsWith(RootPath, StringComparison.Ordinal));
        yield return flat;
    }
}