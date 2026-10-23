using System.Diagnostics;

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
    /// Resolves a package file reference ("factory/FSBL.bin", a bare basename, or a vendor
    /// regex image reference) to an existing file under the package root, rejecting paths
    /// that escape the root.
    /// <para>把包内文件引用（"factory/FSBL.bin"、裸文件名或厂商正则镜像引用）解析为包根
    /// 下的现有文件，拒绝逃出包根的路径。</para>
    /// </summary>
    /// <param name="reference">Reference text from the untrusted package. <para>来自不可信包文件的引用文本。</para></param>
    /// <returns>The resolved file path, or <see langword="null"/> when the reference is a regex image marker or the file does not exist. <para>解析出的文件路径；引用为正则镜像标记或文件不存在时为 <see langword="null"/>。</para></returns>
    public string? ResolveFile(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || IsRegexReference(reference))
        {
            return null;
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
        var trimmed = sizeValue?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 ||
            trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("null", StringComparison.Ordinal))
        {
            return null;
        }

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

    private static bool IsRegexReference(string reference) =>
        reference.Length > 1 && reference[0] == '/' && reference[^1] == '/';

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