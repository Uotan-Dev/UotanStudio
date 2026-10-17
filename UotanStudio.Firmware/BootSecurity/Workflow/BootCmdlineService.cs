using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.BootSecurity.Workflow;

/// <summary>
/// Edits a single token of an Android boot image kernel command line, producing an auditable plan before
/// the image is repacked. Boot images carry <c>androidboot.*</c> parameters in the header, so a change to
/// one of them is a header edit rather than a ramdisk edit.
/// <para>编辑 Android boot 镜像内核命令行中的单个标记，并在重新打包前生成可审计方案。
/// boot 镜像把 <c>androidboot.*</c> 参数放在头部里，因此改动它属于头部编辑而不是 ramdisk 编辑。</para>
/// </summary>
public sealed class BootCmdlineService
{
    /// <summary>
    /// Maximum accepted length of a parameter name.
    /// <para>可接受的参数名最大长度。</para>
    /// </summary>
    /// <value>256 characters. <para>256 个字符。</para></value>
    public const int MaximumKeyLength = 256;

    /// <summary>
    /// Maximum accepted length of a parameter value.
    /// <para>可接受的参数值最大长度。</para>
    /// </summary>
    /// <value>1024 characters. <para>1024 个字符。</para></value>
    public const int MaximumValueLength = 1024;

    /// <summary>
    /// Reads the boot image command line and plans one token replacement or removal without touching the input file.
    /// <para>读取 boot 镜像的命令行，规划一次标记替换或移除，不改动输入文件。</para>
    /// </summary>
    /// <param name="bootPath">Path to the Android boot image to inspect. <para>要检查的 Android boot 镜像路径。</para></param>
    /// <param name="key">Parameter name without its <c>=value</c> part, such as <c>androidboot.selinux</c>.
    /// <para>不含 <c>=value</c> 部分的参数名，例如 <c>androidboot.selinux</c>。</para></param>
    /// <param name="value">Replacement value, or <see langword="null"/> or empty to delete the parameter.
    /// <para>替换值；为 <see langword="null"/> 或空字符串时删除该参数。</para></param>
    /// <returns>A plan carrying the original and updated command lines. Existing tokens with the same name are
    /// removed regardless of case, and a replacement token is appended at the end.
    /// <para>包含原始与更新后命令行的方案；同名旧标记不论大小写都会被移除，替换标记追加在末尾。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is blank or contains whitespace, <c>=</c>,
    /// or a control character, or <paramref name="value"/> contains whitespace or a control character, or
    /// either exceeds the documented bound.
    /// <para><paramref name="key"/> 为空或含空白、<c>=</c> 或控制字符；<paramref name="value"/> 含空白或控制字符；
    /// 或二者超过文档标注的长度上限。</para></exception>
    /// <exception cref="ImageParseException">The boot image at <paramref name="bootPath"/> has an invalid header or magic.
    /// <para><paramref name="bootPath"/> 处的 boot 镜像头部或魔数无效。</para></exception>
    /// <exception cref="FileNotFoundException">No boot image exists at <paramref name="bootPath"/>.
    /// <para><paramref name="bootPath"/> 处不存在 boot 镜像。</para></exception>
    public BootCmdlinePlan CreatePlan(string bootPath, string key, string? value)
    {
        ValidateKey(key);
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalizedValue is not null)
        {
            ValidateValue(normalizedValue);
        }

        var header = BootHeaderSource.Read(bootPath);
        var original = header.CommandLine ?? string.Empty;
        var tokens = original.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !MatchesKey(token, key))
            .ToList();
        if (normalizedValue is not null)
        {
            tokens.Add($"{key}={normalizedValue}");
        }

        return new BootCmdlinePlan(bootPath, key, normalizedValue, original, string.Join(' ', tokens),
        [
            "Edits the boot header command line only; vendor_boot bootconfig, dtboot and cmdline30 aliases are unchanged",
            "The parameter is not interpreted, so a misspelled or unsupported name is written as given",
            "Real-device acceptance of the resulting command line requires separate validation"
        ]);
    }

    /// <summary>
    /// Applies a plan and publishes the repacked boot image, replacing an existing destination file.
    /// <para>应用方案并发布重新打包的 boot 镜像，已存在的目标文件会被替换。</para>
    /// </summary>
    /// <param name="plan">Plan produced by <see cref="CreatePlan"/>. <para>由 <see cref="CreatePlan"/> 生成的方案。</para></param>
    /// <param name="outputPath">Destination file. <para>目标文件。</para></param>
    /// <param name="cancellationToken">Cancels the write; the temporary file is deleted and cancellation is rethrown.
    /// <para>取消写出；临时文件会被删除，取消异常会重新抛出。</para></param>
    /// <returns>A task completed once the repacked image is published. <para>重新打包的镜像发布完成后结束的任务。</para></returns>
    /// <exception cref="ArgumentException">The command line no longer fits the boot header capacity.
    /// <para>命令行已超出 boot 头部的容量。</para></exception>
    /// <exception cref="ImageParseException">The source image of <paramref name="plan"/> cannot be parsed.
    /// <para><paramref name="plan"/> 的源镜像无法解析。</para></exception>
    /// <exception cref="IOException">The temporary write or the move onto <paramref name="outputPath"/> failed.
    /// <para>临时写出或移动到 <paramref name="outputPath"/> 失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task ApplyAsync(BootCmdlinePlan plan, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var parser = new BootImageParser();
        parser.ParseFile(plan.BootPath);
        parser.Header.CommandLine = plan.UpdatedCommandLine;
        var bytes = BootImagePacker.Repack(parser);
        var temporary = TempFileHelper.CreateAdjacentPath(outputPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, outputPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool MatchesKey(string token, string key) => token.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, key, StringComparison.OrdinalIgnoreCase);

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MaximumKeyLength || key.Any(char.IsWhiteSpace) || key.Contains('=') || key.Any(char.IsControl))
        {
            throw new ArgumentException($"A command line parameter name must be at most {MaximumKeyLength} characters without whitespace, '=' or control characters.", nameof(key));
        }
    }

    private static void ValidateValue(string value)
    {
        if (value.Length > MaximumValueLength || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException($"A command line parameter value must be at most {MaximumValueLength} characters without whitespace or control characters.", "value");
        }
    }
}

/// <summary>
/// An auditable single-parameter command line edit produced without touching the source image.
/// <para>在不改动源镜像的前提下生成的单参数命令行可审计编辑方案。</para>
/// </summary>
/// <param name="BootPath">Source boot image the plan was derived from. <para>方案所基于的源 boot 镜像路径。</para></param>
/// <param name="Key">Parameter name being replaced or removed. <para>被替换或移除的参数名。</para></param>
/// <param name="Value">Replacement value, or <see langword="null"/> when the parameter is removed.
/// <para>替换值；移除该参数时为 <see langword="null"/>。</para></param>
/// <param name="OriginalCommandLine">Command line as read from the boot header. <para>从 boot 头部读取的原始命令行。</para></param>
/// <param name="UpdatedCommandLine">Command line after the old token was dropped and the replacement appended.
/// <para>丢弃旧标记并追加替换标记后的命令行。</para></param>
/// <param name="Limitations">Scope caveats attached to the plan. <para>方案附带的作用域限制说明。</para></param>
public sealed record BootCmdlinePlan(string BootPath, string Key, string? Value, string OriginalCommandLine,
    string UpdatedCommandLine, IReadOnlyList<string> Limitations);