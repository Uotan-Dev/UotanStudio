namespace UotanStudio.Tests.Infrastructure;

/// <summary>
/// 真实固件语料库定位器：测试从环境变量指向或本机默认的固件样本目录解析真实文件，
/// 替代硬编码的开发者个人路径。目录缺失时各测试以 <c>Assert.Skip</c> 跳过。
/// <para>Real firmware corpus locator: tests resolve real samples from a corpus root given by
/// environment variable or a local default, replacing hard-coded developer paths. Tests skip
/// via <c>Assert.Skip</c> when the corpus is unavailable.</para>
/// </summary>
/// <remarks>
/// 默认根目录为 <c>D:\Firmware</c>；可用环境变量 <c>UOTAN_FIRMWARE_CORPUS</c> 覆盖。
/// 查找按文件名（大小写不敏感）递归扫描根目录，命中即返回第一个匹配项。
/// <para>The default root is <c>D:\Firmware</c>; the environment variable
/// <c>UOTAN_FIRMWARE_CORPUS</c> overrides it. Lookups scan the root recursively by file name
/// (case-insensitive) and return the first match.</para>
/// </remarks>
internal static class FirmwareCorpus
{
    /// <summary>默认语料根目录。<para>The default corpus root.</para></summary>
    private const string DefaultRoot = @"D:\Firmware";

    /// <summary>语料根目录覆盖环境变量名。<para>The env var name overriding the corpus root.</para></summary>
    private const string RootEnvVar = "UOTAN_FIRMWARE_CORPUS";

    /// <summary>
    /// 语料根目录；未设置且默认目录不存在时为 null。
    /// <para>The corpus root; null when unset and the default directory is missing.</para>
    /// </summary>
    public static string? RootDirectory { get; } = ResolveRoot();

    /// <summary>语料库是否可用。<para>Whether the corpus is available.</para></summary>
    public static bool IsAvailable => RootDirectory is not null;

    /// <summary>
    /// 按文件名（不含路径、大小写不敏感）在语料库中递归查找第一个匹配文件。
    /// 语料不可用或未命中时返回 null。
    /// <para>Recursively finds the first file in the corpus whose name (no directory part,
    /// case-insensitive) equals <paramref name="fileName"/>. Returns null when the corpus is
    /// unavailable or nothing matches.</para>
    /// </summary>
    public static string? FindFile(string fileName)
    {
        var root = RootDirectory;
        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable(RootEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        return Directory.Exists(DefaultRoot) ? DefaultRoot : null;
    }
}