using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Obfuscation;

/// <summary>
/// One user-supplied rename rule: a regular expression selects targets and a template names the
/// replacement. Templates may contain <c>{name}</c> (the original simple name), <c>{class}</c>
/// (the original dotted class name, for members), <c>{counter}</c> (a plain running counter) and
/// <c>{z2}</c>…<c>{z5}</c> (counters zero-padded to N digits, MT-style).
/// <para>一条用户改名规则：正则选择目标，模板命名替换结果。模板可含 <c>{name}</c>（原简单名）、
/// <c>{class}</c>（原点分类名，用于成员）、<c>{counter}</c>（普通递增计数）与 <c>{z2}</c>…<c>{z5}</c>
/// （按 N 位补零的计数，MT 式）。</para>
/// </summary>
/// <param name="Pattern">Regular expression matched against the target's match key.
/// <para>与目标匹配键进行匹配的正则表达式。</para></param>
/// <param name="Template">Replacement template; an empty template rejects renaming for the matched target.
/// <para>替换模板；模板为空表示匹配到的目标不改名。</para></param>
public sealed record RenameRule(string Pattern, string Template)
{
    /// <summary>Compiles the pattern case-sensitively with a 256 ms timeout.
    /// <para>区分大小写编译正则，超时 256 毫秒。</para></summary>
    /// <exception cref="ArgumentException">The pattern is invalid.
    /// <para>正则非法。</para></exception>
    public Regex Compile() => new(Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(256));
}

/// <summary>
/// Options for <see cref="DexRenameEngine"/>.
/// <para><see cref="DexRenameEngine"/> 的选项。</para></summary>
public sealed class DexRenameOptions
{
    /// <summary>User rules applied before the built-in heuristics.
    /// <para>在内置启发式之前应用的用户规则。</para></summary>
    public IReadOnlyList<RenameRule> Rules { get; init; } = [];

    /// <summary>Renames members whose simple name consists only of look-alike characters such as <c>O o 0 l I 1</c>.
    /// <para>改名的成员包括简单名仅由 <c>O o 0 l I 1</c> 等形近字符组成的情形。</para></summary>
    public bool RenameLookalikeNames { get; init; } = true;

    /// <summary>Renames members whose simple name is whitespace-only or invisible.
    /// <para>简单名仅由空白或不可见字符组成的成员参与改名。</para></summary>
    public bool RenameWhitespaceNames { get; init; } = true;

    /// <summary>Template for auto-renamed classes. <para>自动改名的类名模板。</para></summary>
    public string ClassTemplate { get; init; } = "C{z4}";

    /// <summary>Template for auto-renamed methods. <para>自动改名的方法名模板。</para></summary>
    public string MethodTemplate { get; init; } = "m{z4}";

    /// <summary>Template for auto-renamed fields. <para>自动改名的字段名模板。</para></summary>
    public string FieldTemplate { get; init; } = "f{z4}";
}

/// <summary>
/// The rename outcome: counters and the ProGuard-style mapping text.
/// <para>改名结果：计数与 ProGuard 风格 mapping 文本。</para></summary>
public sealed class DexRenameReport
{
    /// <summary>How many classes were renamed. <para>被改名的类数量。</para></summary>
    public int RenamedClasses { get; init; }

    /// <summary>How many methods were renamed. <para>被改名的方法数量。</para></summary>
    public int RenamedMethods { get; init; }

    /// <summary>How many fields were renamed. <para>被改名的字段数量。</para></summary>
    public int RenamedFields { get; init; }

    /// <summary>ProGuard-style mapping lines (<c>original -> new</c>), classes first, members indented.
    /// <para>ProGuard 风格 mapping 行（<c>原 -> 新</c>），类在前、成员缩进。</para></summary>
    public string Mapping { get; init; } = string.Empty;
}

/// <summary>
/// Counter-measure against name obfuscation: detects look-alike and invisible member names
/// (plus user rules) and renames classes and members at the model level, producing a ProGuard-style
/// mapping. Renaming touches only identifier tables — string literals, including reflection
/// strings, are deliberately untouched.
/// <para>名称混淆对抗：检测形近与不可见成员名（外加用户规则），在模型层改类名与成员名并产出
/// ProGuard 风格 mapping。改名只动标识符表——包括反射用的字符串字面量一概不动。</para>
/// </summary>
/// <remarks>
/// Class renames rewrite the <c>type_ids</c> descriptor so every reference (superclass,
/// interfaces, member declarations, protos, annotations and instructions) follows through the type
/// index. Member renames append a fresh string and repoint the member id's name index, so shared
/// string content used as literals is never disturbed.
/// <para>类名改写 <c>type_ids</c> 描述符，全部引用（父类、接口、成员声明、原型、注解与指令）经类型索引自动跟随。
/// 成员改名追加新字符串并重指成员 id 的名称索引，因此被当作字面量共享的字符串内容不受影响。</para>
/// </remarks>
public static class DexRenameEngine
{
    private static readonly Regex LookalikeNameLong = new(@"^[Oo0lI1]{3,}$", RegexOptions.CultureInvariant);

    /// <summary>Renames obfuscated classes and members in place and reports the mapping.
    /// <para>就地改名混淆的类与成员并报告 mapping。</para></summary>
    /// <param name="dex">Model to modify in place. <para>就地修改的模型。</para></param>
    /// <param name="options">Detection and template options; <see langword="null"/> uses the defaults.
    /// <para>检测与模板选项；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <returns>The rename report with the mapping text.
    /// <para>含 mapping 文本的改名报告。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">A user rule's pattern is invalid.
    /// <para>用户规则的 pattern 非法。</para></exception>
    public static DexRenameReport Rename(DexFile dex, DexRenameOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        options ??= new DexRenameOptions();
        var rules = new List<(Regex Regex, string Template)>();
        foreach (var rule in options.Rules)
        {
            rules.Add((rule.Compile(), rule.Template));
        }

        var classCounter = 0;
        var methodCounter = 0;
        var fieldCounter = 0;
        var renamedClasses = 0;
        var renamedMethods = 0;
        var renamedFields = 0;
        var mapping = new StringBuilder();

        foreach (var cls in dex.Classes)
        {
            var originalDescriptor = dex.GetType(cls.TypeIndex);
            var originalDotted = FormatClassName(originalDescriptor);
            var currentDescriptor = originalDescriptor;
            var classLineWritten = false;

            void WriteClassLine()
            {
                if (classLineWritten) return;
                classLineWritten = true;
                mapping.Append(originalDotted).Append(" -> ").Append(FormatClassName(currentDescriptor)).AppendLine(":");
            }

            var newName = ResolveClassName(rules, options, originalDotted, classCounter);
            if (newName is not null)
            {
                currentDescriptor = ReplaceSimpleClassName(originalDescriptor, newName);
                RewriteTypeDescriptor(dex, originalDescriptor, currentDescriptor);
                classCounter++;
                renamedClasses++;
                WriteClassLine();
            }

            foreach (var method in cls.AllMethods)
            {
                var id = dex.GetMethod(method.MethodIndex);
                if (id is null) continue;
                var originalName = dex.GetString(id.NameIndex);
                var memberName = ResolveMemberName(rules, options, options.MethodTemplate, originalDotted, originalName, ref methodCounter);
                if (memberName is null) continue;
                id.NameIndex = AllocateString(dex, memberName);
                renamedMethods++;
                WriteClassLine();
                mapping.Append("    ").Append(DescribeMethod(dex, id, originalName)).Append(" -> ").AppendLine(memberName);
            }

            foreach (var field in cls.AllFields)
            {
                var id = dex.GetField(field.FieldIndex);
                if (id is null) continue;
                var originalName = dex.GetString(id.NameIndex);
                var memberName = ResolveMemberName(rules, options, options.FieldTemplate, originalDotted, originalName, ref fieldCounter);
                if (memberName is null) continue;
                id.NameIndex = AllocateString(dex, memberName);
                renamedFields++;
                WriteClassLine();
                mapping.Append("    ").Append(dex.FormatType(id.TypeIndex)).Append(' ').Append(originalName)
                    .Append(" -> ").AppendLine(memberName);
            }
        }

        return new DexRenameReport
        {
            RenamedClasses = renamedClasses,
            RenamedMethods = renamedMethods,
            RenamedFields = renamedFields,
            Mapping = mapping.ToString().TrimEnd()
        };
    }

    /// <summary>Resolves a new class name from user rules and heuristics; <see langword="null"/> keeps the current name.
    /// <para>按用户规则与启发式解析类新名；为 <see langword="null"/> 表示保持原名。</para></summary>
    private static string? ResolveClassName(
        List<(Regex Regex, string Template)> rules, DexRenameOptions options, string dotted, int counter)
    {
        foreach (var (regex, template) in rules)
        {
            if (!regex.IsMatch(dotted)) continue;
            if (string.IsNullOrEmpty(template)) return null;
            return RenderTemplate(template, SimpleClassName(dotted), dotted, counter);
        }
        if (!IsObfuscatedName(SimpleClassName(dotted), options)) return null;
        return RenderTemplate(options.ClassTemplate, SimpleClassName(dotted), dotted, counter);
    }

    /// <summary>Resolves a new member name from user rules and heuristics; <see langword="null"/> keeps the current name.
    /// <para>按用户规则与启发式解析成员新名；为 <see langword="null"/> 表示保持原名。</para></summary>
    private static string? ResolveMemberName(
        List<(Regex Regex, string Template)> rules, DexRenameOptions options,
        string defaultTemplate, string classKey, string simpleName, ref int counter)
    {
        foreach (var (regex, template) in rules)
        {
            if (!regex.IsMatch($"{classKey}->{simpleName}")) continue;
            if (string.IsNullOrEmpty(template)) return null;
            return RenderTemplate(template, simpleName, classKey, counter++);
        }
        if (!IsObfuscatedName(simpleName, options)) return null;
        return RenderTemplate(defaultTemplate, simpleName, classKey, counter++);
    }

    /// <summary>Reports whether the simple name matches the enabled obfuscation heuristics.
    /// <para>报告简单名是否命中已启用的混淆启发式。</para></summary>
    private static bool IsObfuscatedName(string simpleName, DexRenameOptions options)
    {
        if (simpleName.Length == 0) return false;
        if (options.RenameWhitespaceNames && simpleName.All(char.IsWhiteSpace))
        {
            return true;
        }
        return options.RenameLookalikeNames && LookalikeNameLong.IsMatch(simpleName);
    }

    /// <summary>Expands the template tokens. <para>展开模板标记。</para></summary>
    private static string RenderTemplate(string template, string simpleName, string classKey, int counter)
    {
        return template
            .Replace("{name}", simpleName, StringComparison.Ordinal)
            .Replace("{class}", classKey, StringComparison.Ordinal)
            .Replace("{counter}", counter.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{z2}", (counter % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{z3}", (counter % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{z4}", (counter % 10000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{z5}", (counter % 100000).ToString("00000", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Appends the new string when absent and returns its index.
    /// <para>新字符串不存在时追加，返回其索引。</para></summary>
    private static int AllocateString(DexFile dex, string value)
    {
        var existing = dex.Strings.IndexOf(value);
        if (existing >= 0) return existing;
        dex.Strings.Add(value);
        return dex.Strings.Count - 1;
    }

    /// <summary>Rewrites every class-form type descriptor whose simple class matches, including array forms, and makes sure each rewritten descriptor exists in the string table.
    /// <para>改写所有匹配的类形式类型描述符（含数组形式），并确保每个改写后的描述符都存在于字符串表中。</para></summary>
    private static void RewriteTypeDescriptor(DexFile dex, string originalDescriptor, string newDescriptor)
    {
        for (var i = 0; i < dex.Types.Count; i++)
        {
            var descriptor = dex.Types[i];
            if (descriptor == originalDescriptor)
            {
                dex.Types[i] = newDescriptor;
                AllocateString(dex, newDescriptor);
                continue;
            }
            var depth = 0;
            while (depth < descriptor.Length && descriptor[depth] == '[') depth++;
            if (depth > 0 && string.CompareOrdinal(descriptor, depth, originalDescriptor, 0, originalDescriptor.Length) == 0)
            {
                var rewritten = string.Concat(descriptor[..depth], newDescriptor);
                dex.Types[i] = rewritten;
                AllocateString(dex, rewritten);
            }
        }
    }

    /// <summary>Replaces the simple class part of a class descriptor while keeping the package and array prefix.
    /// <para>替换类描述符的简单类部分，保留包名与数组前缀。</para></summary>
    private static string ReplaceSimpleClassName(string descriptor, string newSimpleName)
    {
        var depth = 0;
        while (depth < descriptor.Length && descriptor[depth] == '[') depth++;
        var body = descriptor[depth..];
        if (body.Length < 3 || body[0] != 'L' || !body.EndsWith(";", StringComparison.Ordinal))
        {
            return descriptor;
        }
        var lastSlash = body.LastIndexOf('/');
        var prefix = lastSlash >= 0 ? body[1..(lastSlash + 1)] : string.Empty;
        return string.Concat(descriptor[..depth], "L", prefix, newSimpleName, ";");
    }

    /// <summary>Formats the dotted class name from a class-form descriptor; non-class forms pass through.
    /// <para>从类形式描述符取点分类名；非类形式原样返回。</para></summary>
    private static string FormatClassName(string descriptor)
    {
        var depth = 0;
        while (depth < descriptor.Length && descriptor[depth] == '[') depth++;
        if (depth >= descriptor.Length || descriptor[depth] != 'L' || !descriptor.EndsWith(";", StringComparison.Ordinal))
        {
            return descriptor;
        }
        return descriptor[(depth + 1)..^1].Replace('/', '.');
    }

    /// <summary>Extracts the simple class name from a dotted or descriptor form.
    /// <para>从点分类名或描述符提取简单类名。</para></summary>
    private static string SimpleClassName(string dotted)
    {
        var dot = dotted.LastIndexOf('.');
        return dot >= 0 ? dotted[(dot + 1)..] : dotted;
    }

    /// <summary>Formats a member as <c>proto name</c> for the mapping.
    /// <para>成员按 <c>原型 名称</c> 输出 mapping。</para></summary>
    private static string DescribeMethod(DexFile dex, MethodId id, string originalName)
    {
        var proto = dex.GetProto(id.ProtoIndex);
        var parameters = proto is null
            ? string.Empty
            : string.Concat(proto.ParameterTypeIndices.Select(t => dex.FormatType(t)));
        var returnType = proto is null ? "?" : dex.FormatType(proto.ReturnTypeIndex);
        return $"{returnType} {originalName}({parameters})";
    }
}