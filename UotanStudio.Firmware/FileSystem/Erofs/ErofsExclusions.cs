using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// Compiled image-relative exclusion rules for the EROFS writer. A literal entry excludes that exact
/// image path and, when it addresses a directory, every entry beneath it; a regular expression
/// excludes any entry whose image path it matches at any position. Both kinds are evaluated against
/// the <c>/</c>-separated image-relative path with no leading slash, so a host separator never
/// reaches the comparison and an exclusion is decided before any ownership, fs_config or
/// extended-attribute table is consulted.
/// <para>EROFS 写入方的镜像内相对路径排除规则。字面条目排除该确切镜像路径，并在它指向目录时排除其下所有条目；
/// 正则表达式在任意位置命中镜像路径即排除该条目。两类规则都针对不带前导斜杠、以 <c>/</c> 分隔的镜像内相对路径求值，
/// 因此宿主分隔符不会进入比较，且排除在查阅任何属主、fs_config 或扩展属性表之前就已判定。</para>
/// </summary>
/// <remarks>
/// The matcher also rejects a descendant whose own path is tested directly, not just one reached by walking
/// into an excluded directory. That distinction matters because the builder hands it an image path on every
/// entry, as well as when a caller tests a path the directory walk never produced, so a directory rule stays
/// transitive instead of depending on the walk order. A regular expression uses the same unanchored,
/// case-sensitive search a POSIX <c>regexec</c> performs.
/// <para>此处的匹配器还会直接拒绝自身路径被测试的后代，而不只排除经由目录下钻到达的条目。该差异有意义：构建器为
/// 每个条目传入镜像路径，调用方也可能测试目录遍历从未产出的路径，因此目录规则保持可传递，而不依赖遍历顺序。
/// 正则表达式使用与 POSIX <c>regexec</c> 相同的非锚定、区分大小写搜索。</para>
/// </remarks>
internal sealed class ErofsExclusions
{
    private static readonly string[] NoPaths = [];

    private readonly string[] literalPaths;
    private readonly Regex[] regexes;
    private readonly string? problem;
    private readonly string? problemParameter;

    private ErofsExclusions(string[] literalPaths, Regex[] regexes, string? problem, string? problemParameter)
    {
        this.literalPaths = literalPaths;
        this.regexes = regexes;
        this.problem = problem;
        this.problemParameter = problemParameter;
    }

    /// <summary>
    /// An exclusion set that excludes nothing, usable where no rules were requested.
    /// <para>不排除任何内容的排除集，在没有请求规则时使用。</para>
    /// </summary>
    internal static ErofsExclusions Empty { get; } = new(NoPaths, [], null, null);

    /// <summary>
    /// Compiles the requested rules. Construction never throws: an entry that is empty or that
    /// normalizes to no path, and a pattern the regular-expression engine rejects, are recorded and
    /// reported by <see cref="Validate"/>, so a caller can either fail the build deliberately or
    /// inspect the rules before deciding. The offending entry is left out of the usable rules while
    /// every valid entry still applies.
    /// <para>编译请求的规则。构造过程从不抛出：空条目、规范化为空路径的条目，以及正则引擎拒绝的模式都会被记录，
    /// 并由 <see cref="Validate"/> 报告，因此调用方可以选择让构建失败，也可以先检查规则再决定。有问题的条目不会
    /// 进入可用规则，但每条合法条目仍然生效。</para>
    /// </summary>
    /// <param name="literalPaths">Image-relative literal paths to exclude, or <see langword="null"/> for none.
    /// <para>要排除的镜像内字面路径；无则为 <see langword="null"/>。</para></param>
    /// <param name="regexes">Regular expression patterns to match against image-relative paths, or
    /// <see langword="null"/> for none. <para>用于匹配镜像内相对路径的正则模式；无则为 <see langword="null"/>。</para></param>
    internal ErofsExclusions(IReadOnlyList<string>? literalPaths, IReadOnlyList<string>? regexes)
    {
        var acceptedPaths = new List<string>();
        var acceptedRegexes = new List<Regex>();
        string? problem = null;
        string? problemParameter = null;

        foreach (var entry in literalPaths ?? Array.Empty<string>())
        {
            var normalized = Normalize(entry);
            if (normalized.Length == 0)
            {
                if (problem is null)
                {
                    problem = $"The EROFS exclusion path entry '{entry}' is empty or names no image path; "
                        + "an entry that matches nothing must be removed instead of shipping an empty rule.";
                    problemParameter = nameof(literalPaths);
                }

                continue;
            }

            acceptedPaths.Add(normalized);
        }

        foreach (var pattern in regexes ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                if (problem is null)
                {
                    problem = $"The EROFS exclusion regex entry '{pattern}' is empty or whitespace-only; "
                        + "an empty pattern would match every image path and must be removed.";
                    problemParameter = nameof(regexes);
                }

                continue;
            }

            try
            {
                acceptedRegexes.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException error)
            {
                if (problem is null)
                {
                    problem = $"The EROFS exclusion regex '{pattern}' is not a valid regular expression: "
                        + error.Message;
                    problemParameter = nameof(regexes);
                }
            }
        }

        this.literalPaths = [.. acceptedPaths];
        this.regexes = [.. acceptedRegexes];
        this.problem = problem;
        this.problemParameter = problemParameter;
    }

    /// <summary>
    /// Whether no usable rule was requested.
    /// <para>是否没有请求任何可用规则。</para>
    /// </summary>
    /// <value><see langword="true"/> when neither a literal path nor a regex was accepted.
    /// <para>既没有接受字面路径也没有接受正则时为 <see langword="true"/>。</para></value>
    internal bool IsEmpty => literalPaths.Length == 0 && regexes.Length == 0;

    /// <summary>
    /// Reports whether one image-relative path is excluded, matching a literal entry exactly, matching a
    /// literal entry as a directory prefix, or matching any regular expression at any position.
    /// <para>判断某个镜像内相对路径是否被排除：与字面条目完全一致、以字面条目为目录前缀，或在任意位置命中任一
    /// 正则表达式。</para>
    /// </summary>
    /// <param name="imagePath">Image-relative path, which may use either separator; <see langword="null"/>, an
    /// empty string and a path that normalizes to nothing match no rule because the build root is never excluded.
    /// <para>镜像内相对路径，可含任一分隔符；<see langword="null"/>、空字符串以及规范化为空的路径不命中任何规则，
    /// 因为构建根绝不会被排除。</para></param>
    /// <returns><see langword="true"/> when a rule excludes the path.
    /// <para>有规则排除该路径时为 <see langword="true"/>。</para></returns>
    internal bool IsExcluded(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return false;
        }

        var path = Normalize(imagePath);
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var literal in literalPaths)
        {
            if (path.Equals(literal, StringComparison.Ordinal))
            {
                return true;
            }

            if (path.Length > literal.Length && path[literal.Length] == '/'
                && path.AsSpan(0, literal.Length).SequenceEqual(literal))
            {
                return true;
            }
        }

        foreach (var regex in regexes)
        {
            if (regex.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fails the build on the first rule that cannot be used, naming it in the message so a typo is not
    /// silently ignored. A literal entry that is empty or normalizes to no path, and a pattern the
    /// regular-expression engine rejects or that is whitespace-only, both throw.
    /// <para>遇到第一条不可用规则时使构建失败，并在消息中点名该规则，使拼写错误不会被静默忽略。空条目、规范化为空
    /// 路径的字面条目、正则引擎拒绝的模式以及仅含空白的模式都会抛出。</para>
    /// </summary>
    /// <exception cref="ArgumentException">A literal path entry is empty or names no path, or a regex entry is
    /// whitespace-only or not a valid pattern. <para>字面路径条目为空或未指名路径，或正则条目仅含空白或不是合法
    /// 模式。</para></exception>
    internal void Validate()
    {
        if (problem is not null)
        {
            throw new ArgumentException(problem, problemParameter);
        }
    }

    /// <summary>
    /// Reduces a path to the canonical comparison form: host separators become <c>/</c>, empty segments
    /// disappear, and <c>.</c> segments are dropped, so a leading slash, a doubled slash and a
    /// <c>./</c> prefix all compare equal to the plain image-relative path.
    /// <para>把路径规约为标准比较形式：宿主分隔符变为 <c>/</c>，空段消失，<c>.</c> 段被丢弃，因此前导斜杠、
    /// 连续斜杠与 <c>./</c> 前缀都与普通镜像内相对路径比较相等。</para>
    /// </summary>
    /// <param name="path">Path text to normalize. <para>待规范化的路径文本。</para></param>
    /// <returns>The normalized path, or an empty string when no segment remains.
    /// <para>规范化后的路径；无剩余段时为空字符串。</para></returns>
    internal static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var replaced = path.Replace('\\', '/');
        var segments = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment != ".")
            {
                kept.Add(segment);
            }
        }

        return string.Join('/', kept);
    }
}