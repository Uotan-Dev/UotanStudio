using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// Compiled <c>--compress-hints</c> rules for the EROFS writer.
/// Each accepted line names a physical cluster size in bytes, an optional compression configuration index, and
/// a match pattern; the first rule whose pattern matches a node's image-relative path decides that node's codec
/// and pcluster size, and a zero pcluster size means the node is not compressed. Paths are matched through the
/// same normalization the exclusion matcher uses.
/// <para>EROFS 写入方的 <c>--compress-hints</c> 规则。每条被接受的
/// 行给出物理簇字节数、可选的压缩配置下标与匹配模式；首个模式命中节点镜像内相对路径的规则决定该节点的编解码器
/// 与 pcluster 大小，pcluster 大小为 0 表示该节点不压缩。路径经与排除匹配器相同的规整后比较。</para>
/// </summary>
/// <remarks>
/// Match patterns are compiled after the POSIX translation in
/// <see cref="ErofsExclusions.TranslatePosixPattern"/>: POSIX character classes such as <c>[[:alpha:]]</c>
/// become their ASCII ranges, and an escaped alphanumeric such as <c>\d</c> is the literal character the POSIX
/// ERE grammar makes it rather than a .NET shorthand. A pattern the translation or the engine rejects is
/// recorded as a build warning and the entry is skipped rather than failing the build, unlike
/// <see cref="ErofsExclusions"/>, which fails on an invalid pattern: an exclusion that matches nothing
/// silently ships files the caller asked to keep out, while a skipped hint only leaves one file on the global
/// codec, so the two callers need different failure modes.
/// <para>匹配模式在经 <see cref="ErofsExclusions.TranslatePosixPattern"/> 的 POSIX 翻译后编译：
/// <c>[[:alpha:]]</c> 这类 POSIX 字符类变为其 ASCII 区间，而 <c>\d</c> 这类转义字母数字按 POSIX ERE 文法
/// 就是该字面字符，而不是 .NET 简写。翻译或引擎拒绝的模式记为构建期警告并跳过该条而不让构建失败，这与
/// <see cref="ErofsExclusions"/> 对非法模式让构建失败是有意不同：匹配不到任何东西的排除规则会静默放出调用方
/// 要求排除的文件，而被跳过的提示只让一个文件落在全局编解码器上，因此两者需要不同的失败模式。</para>
/// A hint whose pcluster size exceeds the current maximum raises <see cref="ErofsOptions.MaxPclusterSize"/>
/// and every codec record below that size to the requested value and records a build warning instead of
/// failing; the planner and the compression configuration
/// read the raised limit from those options, so a caller must compile the hints before it materializes the
/// configuration records that carry each codec's maximum. A raise past the 1 MiB format maximum fails the
/// build because the declared layout cannot express it.
/// <para>pcluster 大小超过当前上限的提示会把它以及所有低于该值的编解码器记录提升到请求值并记录一条构建期警告，
/// 而不是失败；规划器与压缩配置都从这些选项读取提升后的上限，因此调用方
/// 必须在生成携带各编解码器上限的配置记录之前编译提示。提升到 1 MiB 格式上限之上会让构建失败，因为声明的布局
/// 无法表达它。</para>
/// </remarks>
internal sealed class ErofsCompressHints
{
    private readonly Rule[] rules;

    private ErofsCompressHints(Rule[] rules) => this.rules = rules;

    /// <summary>A hint set that matches nothing, usable where no rules were requested.
    /// <para>不命中任何内容的提示集，在没有请求规则时使用。</para>
    /// </summary>
    internal static ErofsCompressHints Empty { get; } = new([]);

    /// <summary>Whether no usable rule was requested.
    /// <para>是否没有请求任何可用规则。</para>
    /// </summary>
    /// <value><see langword="true"/> when every line was skipped or none was supplied.
    /// <para>所有行都被跳过或未提供任何行时为 <see langword="true"/>。</para></value>
    internal bool IsEmpty => rules.Length == 0;

    /// <summary>
    /// Parses and compiles the requested rules. A line that names no pattern, whose pattern the POSIX
    /// translation or the engine rejects, or whose pcluster size is not a multiple of the block size, is
    /// recorded as a build warning and skipped; an algorithm index outside the build's configured codecs fails
    /// because it cannot be honored without misreporting the image. A pcluster size above the current maximum
    /// raises <see cref="ErofsOptions.MaxPclusterSize"/> and every codec record below it and records a warning,
    /// while a raise past the 1 MiB format maximum fails the build.
    /// <para>解析并编译请求的规则。未给出模式、模式被 POSIX 翻译或引擎拒绝、或 pcluster 大小不是块大小整数倍的
    /// 行记为构建期警告并跳过；算法下标超出本次构建已配置的编解码器，则让构建失败，因为无法在不误报镜像的前提下
    /// 满足它。pcluster 大小超过当前上限时会提升 <see cref="ErofsOptions.MaxPclusterSize"/> 与所有低于该值的
    /// 编解码器记录并记录一条警告，而提升到 1 MiB 格式上限之上则让构建失败。</para>
    /// </summary>
    /// <param name="lines">Rule lines, or <see langword="null"/> for none.
    /// <para>规则行；无则为 <see langword="null"/>。</para></param>
    /// <param name="options">Options whose warnings are recorded, whose codec table and block size apply, and
    /// whose maximum pcluster size a larger hint raises; the instance is mutated, so a caller must not reuse it
    /// when it still needs the original limit. <para>记录警告、提供编解码器表与块大小、并由更大的提示提升其最大
    /// pcluster 大小的选项；该实例会被就地修改，调用方若仍需原上限就不得复用。</para></param>
    /// <returns>The compiled rules, or <see cref="Empty"/> when none was accepted.
    /// <para>编译后的规则；无被接受时为 <see cref="Empty"/>。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">A rule names an unknown compression
    /// configuration, or a raise cannot be honored because the requested size is above the 1 MiB format maximum.
    /// <para>某条规则指名未知的压缩配置，或无法完成提升：请求的大小超过 1 MiB 格式上限。</para></exception>
    internal static ErofsCompressHints Compile(IReadOnlyList<string>? lines, ErofsOptions options)
    {
        if (lines is not { Count: > 0 })
        {
            return Empty;
        }

        var blockSize = (uint)options.BlockSize;
        var buildMaximum = options.MaxPclusterSize > 0 ? options.MaxPclusterSize : options.BlockSize;
        ErofsOptions[] codecs = options.SourceCompressionOptions is { Count: > 0 } table
            ? [.. table.OrderBy(pair => pair.Key).Select(pair => pair.Value)]
            : [options];
        var accepted = new List<Rule>();

        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? string.Empty;
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var firstEnd = IndexOfWhitespace(line, 0);
            var sizeText = firstEnd < 0 ? line : line[..firstEnd];
            var restStart = firstEnd < 0 ? line.Length : SkipWhitespace(line, firstEnd);
            if (restStart >= line.Length)
            {
                options.AddBuildWarning($"EROFS compress hint '{line}' names no match pattern; the entry was skipped.");
                continue;
            }

            var secondEnd = IndexOfWhitespace(line, restStart);
            var second = secondEnd < 0 ? line[restStart..] : line[restStart..secondEnd];
            var patternStart = secondEnd < 0 ? line.Length : SkipWhitespace(line, secondEnd);
            string pattern;
            uint algorithmIndex;
            if (patternStart >= line.Length)
            {
                pattern = second;
                algorithmIndex = 0;
            }
            else
            {
                pattern = line[patternStart..].TrimEnd();
                algorithmIndex = ParseAtoi(second);
            }

            var pclusterSize = ParseAtoi(sizeText);
            if (pclusterSize % blockSize != 0)
            {
                options.AddBuildWarning(
                    $"EROFS compress hint '{line}' names a {pclusterSize}-byte physical cluster size that is not a "
                    + $"multiple of the {blockSize}-byte block size; the entry was skipped.");
                continue;
            }

            if (pattern.Length == 0)
            {
                options.AddBuildWarning($"EROFS compress hint '{line}' names no match pattern; the entry was skipped.");
                continue;
            }

            if (algorithmIndex >= (uint)codecs.Length)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS compress hint '{line}' names compression configuration {algorithmIndex}, but the build "
                    + $"configures {codecs.Length} codec(s).");
            }

            Regex compiled;
            try
            {
                compiled = new Regex(
                    ErofsExclusions.TranslatePosixPattern(pattern), RegexOptions.CultureInvariant);
            }
            catch (ArgumentException error)
            {
                options.AddBuildWarning(
                    $"EROFS compress hint '{line}' has pattern '{pattern}' that is not a usable regular expression: "
                    + error.Message);
                continue;
            }

            var codecOptions = codecs[algorithmIndex];
            var codecMaximum = codecOptions.MaxPclusterSize > 0 ? codecOptions.MaxPclusterSize : options.BlockSize;
            if (pclusterSize > (uint)buildMaximum)
            {
                if (pclusterSize > (uint)ErofsOptions.MaximumPclusterSize)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS compress hint '{line}' requests a {pclusterSize}-byte pcluster size above the "
                        + $"{ErofsOptions.MaximumPclusterSize}-byte (1 MiB) format maximum.");
                }

                var previousMaximum = buildMaximum;
                buildMaximum = (int)pclusterSize;
                options.MaxPclusterSize = buildMaximum;
                foreach (var codec in codecs)
                {
                    if (codec.MaxPclusterSize < buildMaximum)
                    {
                        codec.MaxPclusterSize = buildMaximum;
                    }
                }

                options.AddBuildWarning(
                    $"EROFS compress hint '{line}' requests a {pclusterSize}-byte pcluster size above the build's "
                    + $"previous maximum of {previousMaximum} bytes; the build and codec maximum was raised to "
                    + $"{pclusterSize} bytes.");
            }
            else if (pclusterSize > (uint)codecMaximum)
            {
                var previousMaximum = codecMaximum;
                codecOptions.MaxPclusterSize = (int)pclusterSize;
                options.AddBuildWarning(
                    $"EROFS compress hint '{line}' requests a {pclusterSize}-byte pcluster size above compression "
                    + $"configuration {algorithmIndex}'s maximum of {previousMaximum} bytes; the codec maximum was "
                    + $"raised to {pclusterSize} bytes.");
            }

            accepted.Add(new Rule(compiled, (int)pclusterSize, codecOptions.Algorithm));
        }

        return accepted.Count == 0 ? Empty : new ErofsCompressHints([.. accepted]);
    }

    /// <summary>
    /// Reports whether any rule matches one image-relative path and returns the first match.
    /// <para>判断是否有规则命中某个镜像内相对路径并返回首个命中。</para>
    /// </summary>
    /// <param name="imagePath">Image-relative path, which may use either separator.
    /// <para>镜像内相对路径，可含任一分隔符。</para></param>
    /// <param name="match">The first matching rule's outcome, or the default value when none matches.
    /// <para>首个命中规则的结果；无命中时为默认值。</para></param>
    /// <returns><see langword="true"/> when a rule matched.
    /// <para>有规则命中时为 <see langword="true"/>。</para></returns>
    internal bool TryMatch(string? imagePath, out Match match)
    {
        match = default;
        var path = ErofsExclusions.Normalize(imagePath ?? string.Empty);
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(path))
            {
                match = new Match(rule.PclusterSize, rule.Algorithm);
                return true;
            }
        }

        return false;
    }

    private static int IndexOfWhitespace(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static uint ParseAtoi(string text)
    {
        var value = 0UL;
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                break;
            }

            value = Math.Min(value * 10 + (uint)(character - '0'), uint.MaxValue);
        }

        return (uint)value;
    }

    /// <summary>One compiled rule, evaluated in file order.
    /// <para>一条编译后的规则，按文件顺序求值。</para>
    /// </summary>
    /// <param name="Pattern">Compiled match pattern. <para>编译后的匹配模式。</para></param>
    /// <param name="PclusterSize">Selected physical cluster size in bytes; zero means no compression.
    /// <para>选定的物理簇字节数；0 表示不压缩。</para></param>
    /// <param name="Algorithm">Codec the selected compression configuration uses.
    /// <para>所选压缩配置使用的编解码器。</para></param>
    private readonly record struct Rule(Regex Pattern, int PclusterSize, ErofsCompressionAlgorithm Algorithm);

    /// <summary>Outcome of the first matching rule.
    /// <para>首个命中规则的结果。</para>
    /// </summary>
    /// <param name="PclusterSize">Selected physical cluster size in bytes.
    /// <para>选定的物理簇字节数。</para></param>
    /// <param name="Algorithm">Selected codec. <para>选定的编解码器。</para></param>
    internal readonly record struct Match(int PclusterSize, ErofsCompressionAlgorithm Algorithm)
    {
        /// <summary>Whether the matched rule disables compression for the node.
        /// <para>命中的规则是否让该节点不压缩。</para>
        /// </summary>
        /// <value><see langword="true"/> when the rule named a zero pcluster size.
        /// <para>规则给出的 pcluster 大小为 0 时为 <see langword="true"/>。</para></value>
        internal bool Disabled => PclusterSize == 0;
    }
}