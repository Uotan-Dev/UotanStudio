using System.Text;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// Compiled image-relative exclusion rules for the EROFS writer.
/// A literal entry excludes exactly that image path, compared with the
/// ordinal equivalent of <c>strcmp</c>; a regular expression excludes any entry whose image path it matches
/// at any position. Both kinds are evaluated against the <c>/</c>-separated image-relative path with no
/// leading slash, so a host separator never reaches the comparison and an exclusion is decided before any
/// ownership, fs_config or extended-attribute table is consulted.
/// <para>EROFS 写入方的镜像内相对路径排除规则。字面条目只排除该确切
/// 镜像路径，比较等价于 <c>strcmp</c> 的序数比较；正则表达式在任意位置命中镜像路径即排除该条目。两类规则都
/// 针对不带前导斜杠、以 <c>/</c> 分隔的镜像内相对路径求值，因此宿主分隔符不会进入比较，且排除在查阅任何属主、
/// fs_config 或扩展属性表之前就已判定。</para>
/// </summary>
/// <remarks>
/// A literal rule does not expand to descendants: the comparison is exact, so a descendant path
/// tested directly is not excluded by an ancestor's literal entry. The writer still prunes an excluded
/// directory during collection because it never descends into a node it dropped, which is why a literal
/// directory entry omits the whole subtree in a build; a caller that needs recursive exclusion of
/// separately tested paths must supply a regular expression such as <c>^system/etc(/|$)</c> or list every
/// entry. Regular expressions follow POSIX extended syntax after the translation described on
/// <see cref="TranslatePosixPattern"/>, and match with the case-sensitive, unanchored search a POSIX
/// <c>regexec</c> performs.
/// <para>字面规则不会扩展到后代：比较的是确切路径，因此直接测试某个后代路径时不会因其祖先的字面条目而被
/// 排除。写入方在收集阶段仍会剪掉被排除的目录，因为它不会下钻已丢弃的节点，所以在一次构建中字面目录条目会
/// 省略整棵子树；若调用方需要对分别测试的路径做递归排除，必须提供形如 <c>^system/etc(/|$)</c> 的正则或穷举
/// 每个条目。正则经 <see cref="TranslatePosixPattern"/> 所述翻译后遵循 POSIX 扩展语法，并以 POSIX
/// <c>regexec</c> 那样区分大小写、非锚定的搜索方式匹配。</para>
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
    /// normalizes to no path, and a pattern the POSIX translation or the regular-expression engine
    /// rejects, are recorded and reported by <see cref="Validate"/>, so a caller can either fail the build
    /// deliberately or inspect the rules before deciding. The offending entry is left out of the usable
    /// rules while every valid entry still applies.
    /// <para>编译请求的规则。构造过程从不抛出：空条目、规范化为空路径的条目，以及 POSIX 翻译或正则引擎拒绝的模式
    /// 都会被记录，并由 <see cref="Validate"/> 报告，因此调用方可以选择让构建失败，也可以先检查规则再决定。
    /// 有问题的条目不会进入可用规则，但每条合法条目仍然生效。</para>
    /// </summary>
    /// <param name="literalPaths">Image-relative literal paths to exclude, or <see langword="null"/> for none;
    /// an entry matches that exact path only. <para>要排除的镜像内字面路径；无则为 <see langword="null"/>；
    /// 每个条目只匹配该确切路径。</para></param>
    /// <param name="regexes">POSIX extended regular expression patterns to match against image-relative paths,
    /// or <see langword="null"/> for none; see <see cref="TranslatePosixPattern"/> for the translated syntax.
    /// <para>用于匹配镜像内相对路径的 POSIX 扩展正则模式；无则为 <see langword="null"/>；翻译后的语法见
    /// <see cref="TranslatePosixPattern"/>。</para></param>
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
                acceptedRegexes.Add(new Regex(
                    TranslatePosixPattern(pattern), RegexOptions.Compiled | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException error)
            {
                if (problem is null)
                {
                    problem = $"The EROFS exclusion regex '{pattern}' is not a usable POSIX extended regular "
                        + $"expression: {error.Message}";
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
    /// Reports whether one image-relative path is excluded, matching a literal entry exactly, the way
    /// <c>strcmp</c> compares it, or matching any regular expression at any position.
    /// <para>判断某个镜像内相对路径是否被排除：与字面条目完全一致（即 <c>strcmp</c> 的比较方式），或在任意
    /// 位置命中任一正则表达式。</para>
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
    /// silently ignored. A literal entry that is empty or normalizes to no path, and a pattern the POSIX
    /// translation or the regular-expression engine rejects or that is whitespace-only, both throw.
    /// <para>遇到第一条不可用规则时使构建失败，并在消息中点名该规则，使拼写错误不会被静默忽略。空条目、规范化为空
    /// 路径的字面条目、POSIX 翻译或正则引擎拒绝的模式以及仅含空白的模式都会抛出。</para>
    /// </summary>
    /// <exception cref="ArgumentException">A literal path entry is empty or names no path, or a regex entry is
    /// whitespace-only or not a usable POSIX extended regular expression. <para>字面路径条目为空或未指名路径，或
    /// 正则条目仅含空白或不是可用的 POSIX 扩展正则。</para></exception>
    internal void Validate()
    {
        if (problem is not null)
        {
            throw new ArgumentException(problem, problemParameter);
        }
    }

    /// <summary>
    /// Translates one POSIX extended regular expression into the .NET syntax both the exclusion matcher and
    /// the compression-hint matcher compile. A POSIX character class inside a bracket expression becomes the
    /// equivalent ASCII range of the C locale, and a backslash before an ASCII alphanumeric is dropped so that
    /// character is literal, which is how POSIX ERE reads <c>\d</c>; every other construct is passed through.
    /// <para>把一条 POSIX 扩展正则翻译成排除匹配器与压缩提示匹配器所编译的 .NET 语法。方括号表达式中的 POSIX
    /// 字符类变为 C locale 的等价 ASCII 区间，ASCII 字母数字前的反斜杠被丢弃而使其成为字面字符——这正是 POSIX
    /// ERE 读取 <c>\d</c> 的方式；其余构造原样传递。</para>
    /// </summary>
    /// <remarks>
    /// The accepted classes are <c>alpha</c>, <c>digit</c>, <c>alnum</c>, <c>space</c>, <c>xdigit</c>,
    /// <c>upper</c>, <c>lower</c>, <c>punct</c>, <c>blank</c>, <c>cntrl</c>, <c>graph</c> and <c>print</c>,
    /// each expanded over 7-bit ASCII. An unknown class name, a malformed class token, a POSIX collating or
    /// equivalence construct (<c>[.</c> / <c>[=</c>) and a lone trailing backslash are rejected because .NET
    /// would otherwise read them with different semantics. Syntax .NET accepts but POSIX ERE lacks, such as
    /// inline options <c>(?i)</c>, lazy quantifiers or class subtraction, is not rejected and keeps its .NET
    /// meaning, so callers stay within the supported subset.
    /// <para>接受的类为 <c>alpha</c>、<c>digit</c>、<c>alnum</c>、<c>space</c>、<c>xdigit</c>、
    /// <c>upper</c>、<c>lower</c>、<c>punct</c>、<c>blank</c>、<c>cntrl</c>、<c>graph</c> 与 <c>print</c>，
    /// 每个都展开为 7 位 ASCII 范围。未知类名、格式错误的类记号、POSIX 排序或等价构造（<c>[.</c> / <c>[=</c>）
    /// 以及结尾孤立的反斜杠会被拒绝，否则 .NET 会以不同语义读取它们。.NET 接受而 POSIX ERE 没有的语法
    /// （如内联选项 <c>(?i)</c>、懒惰量词或字符类减法）不会被拒绝并保持 .NET 含义，因此调用方应停留在受支持的
    /// 子集内。</para>
    /// </remarks>
    /// <param name="pattern">POSIX extended regular expression source.
    /// <para>POSIX 扩展正则源码。</para></param>
    /// <returns>Regex source the .NET engine reads with the POSIX meaning.
    /// <para>.NET 引擎可按 POSIX 含义读取的正则源码。</para></returns>
    /// <exception cref="ArgumentException">The pattern contains a construct whose POSIX meaning cannot be
    /// preserved. <para>模式包含无法保持 POSIX 含义的构造。</para></exception>
    internal static string TranslatePosixPattern(string pattern)
    {
        var translated = new StringBuilder(pattern.Length);
        var inBracket = false;
        var bracketStart = false;
        var index = 0;
        while (index < pattern.Length)
        {
            var character = pattern[index];
            if (character == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    throw new ArgumentException(
                        "The pattern ends with a lone backslash, which a POSIX extended regular expression "
                        + "does not allow.");
                }

                var escaped = pattern[index + 1];
                if (char.IsAsciiLetterOrDigit(escaped))
                {
                    translated.Append(escaped);
                }
                else
                {
                    translated.Append(character).Append(escaped);
                }

                index += 2;
                bracketStart = false;
                continue;
            }

            if (!inBracket)
            {
                translated.Append(character);
                index++;
                if (character == '[')
                {
                    inBracket = true;
                    bracketStart = true;
                }

                continue;
            }

            if (bracketStart && character is ('^' or ']'))
            {
                translated.Append(character);
                index++;
                if (character == ']')
                {
                    bracketStart = false;
                }

                continue;
            }

            if (character == '[' && index + 1 < pattern.Length
                && pattern[index + 1] is (':' or '.' or '='))
            {
                if (pattern[index + 1] != ':')
                {
                    throw new ArgumentException(
                        $"The POSIX bracket construct '[{pattern[index + 1]}' is not supported; escape the "
                        + "bracket to match it literally.");
                }

                var close = pattern.IndexOf(":]", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new ArgumentException("A POSIX character class is missing its closing ':]'.");
                }

                var name = pattern[(index + 2)..close];
                var body = PosixClassBody(name);
                if (body is null)
                {
                    throw new ArgumentException(
                        $"The POSIX character class '[[:{name}:]]' is not one this matcher supports.");
                }

                translated.Append(body);
                index = close + 2;
                bracketStart = false;
                continue;
            }

            if (character == ']')
            {
                translated.Append(character);
                inBracket = false;
                bracketStart = false;
                index++;
                continue;
            }

            translated.Append(character);
            bracketStart = false;
            index++;
        }

        return translated.ToString();
    }

    private static string? PosixClassBody(string name)
        => name switch
        {
            "alpha" => "A-Za-z",
            "digit" => "0-9",
            "alnum" => "A-Za-z0-9",
            "space" => " \\t-\\r",
            "xdigit" => "0-9A-Fa-f",
            "upper" => "A-Z",
            "lower" => "a-z",
            "punct" => "!-/:-@[-`{-~",
            "blank" => " \\t",
            "cntrl" => "\\x00-\\x1F\\x7F",
            "graph" => "!-~",
            "print" => " -~",
            _ => null,
        };

    /// <summary>
    /// Reduces a path to the canonical comparison form: host separators become <c>/</c>, empty segments
    /// disappear, and <c>.</c> segments are dropped, so a leading slash, a doubled slash and a
    /// <c>./</c> prefix all compare equal to the plain image-relative path.
    /// <para>把路径规约为标准比较形式：宿主分隔符变为 <c>/</c>，空段消失，<c>.</c> 段被丢弃，因此开头的斜杠、
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