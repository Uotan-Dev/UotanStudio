using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

/// <summary>
/// Target SELinux mode for a boot command-line switch.
/// <para>SELinux 命令行切换的目标模式。</para>
/// </summary>
public enum SelinuxSwitchMode
{
    /// <summary>Adds <c>androidboot.selinux=permissive</c> to the command line. <para>向命令行追加 <c>androidboot.selinux=permissive</c>。</para></summary>
    Permissive,

    /// <summary>Adds <c>androidboot.selinux=enforcing</c> to the command line. <para>向命令行追加 <c>androidboot.selinux=enforcing</c>。</para></summary>
    Enforcing,

    /// <summary>Strips SELinux tokens from the command line without adding a replacement. <para>从命令行移除 SELinux 相关标记，不追加替代项。</para></summary>
    Disable
}

/// <summary>Produces an auditable SELinux command-line switch plan and optionally applies it to boot.
/// <para>生成可审计的 SELinux 命令行切换方案，并可选择将其应用到 boot 镜像。</para></summary>
public sealed class SelinuxSwitchService
{
    /// <summary>
    /// Reads the boot image command line and builds an immutable switch plan without modifying the input file.
    /// <para>读取 boot 镜像的命令行并构建不可变的切换方案，不修改输入文件。</para>
    /// </summary>
    /// <param name="bootPath">Path to the Android boot image to inspect. <para>要检查的 Android boot 镜像路径。</para></param>
    /// <param name="mode">Target SELinux mode for the updated command line. <para>更新后命令行采用的目标 SELinux 模式。</para></param>
    /// <returns>
    /// A plan carrying the original and updated command lines; existing <c>androidboot.selinux=…</c> and
    /// <c>selinux=0</c> tokens are removed first, and for non-<see cref="SelinuxSwitchMode.Disable"/>
    /// modes the corresponding <c>androidboot.selinux=…</c> token is appended.
    /// <para>包含原始与更新后命令行的方案；先移除既有的 <c>androidboot.selinux=…</c> 和 <c>selinux=0</c>
    /// 标记，非 <see cref="SelinuxSwitchMode.Disable"/> 模式再追加对应的 <c>androidboot.selinux=…</c> 标记。</para>
    /// </returns>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.ImageParseException">
    /// The boot image at <paramref name="bootPath"/> has an invalid header or magic.
    /// <para><paramref name="bootPath"/> 处的 boot 镜像头部或魔数无效。</para></exception>
    /// <exception cref="FileNotFoundException">No boot image exists at <paramref name="bootPath"/>.
    /// <para><paramref name="bootPath"/> 处不存在 boot 镜像。</para></exception>
    public SelinuxSwitchPlan CreatePlan(string bootPath, SelinuxSwitchMode mode)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(bootPath);
        var original = parser.Header.CommandLine ?? string.Empty;
        var tokens = original.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("androidboot.selinux=", StringComparison.OrdinalIgnoreCase)
                     && !x.Equals("selinux=0", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mode != SelinuxSwitchMode.Disable)
            tokens.Add($"androidboot.selinux={(mode == SelinuxSwitchMode.Permissive ? "permissive" : "enforcing")}");
        var updated = string.Join(' ', tokens);
        return new SelinuxSwitchPlan(bootPath, mode, original, updated,
            ["boot header command line only", "SELinux policy binaries and file_contexts are unchanged", "real-device behavior requires separate validation"]);
    }

    /// <summary>
    /// Applies a plan produced by <see cref="CreatePlan"/> and writes the repacked boot image to the output path.
    /// <para>应用 <see cref="CreatePlan"/> 生成的方案，并将重新打包的 boot 镜像写出到输出路径。</para>
    /// </summary>
    /// <param name="plan">The plan produced by <see cref="CreatePlan"/>. <para>由 <see cref="CreatePlan"/> 生成的方案。</para></param>
    /// <param name="outputPath">Destination file; an existing file is replaced. <para>目标文件；已存在的文件会被替换。</para></param>
    /// <param name="cancellationToken">Cancels the write; the temporary file is deleted and the cancellation is rethrown.
    /// <para>取消写出；临时文件会被删除，取消异常会重新抛出。</para></param>
    /// <returns>A task completed once the repacked image is published. <para>重新打包的镜像发布完成后完成的任务。</para></returns>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.ImageParseException">
    /// The boot image at <c>plan.BootPath</c> cannot be parsed.
    /// <para><c>plan.BootPath</c> 处的 boot 镜像无法解析。</para></exception>
    /// <exception cref="IOException">The temporary write or the move onto <paramref name="outputPath"/> failed.
    /// <para>临时写出或在移动到 <paramref name="outputPath"/> 时失败。</para></exception>
    public async Task ApplyAsync(SelinuxSwitchPlan plan, string outputPath, CancellationToken cancellationToken = default)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(plan.BootPath);
        parser.Header.CommandLine = plan.UpdatedCommandLine;
        var bytes = BootImagePacker.Repack(parser);
        var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, outputPath, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}

/// <summary>
/// An auditable SELinux command-line switch plan produced without touching the source image.
/// <para>在不改动源镜像的前提下生成的可审计 SELinux 命令行切换方案。</para>
/// </summary>
/// <param name="BootPath">Source boot image the plan was derived from. <para>方案所基于的源 boot 镜像路径。</para></param>
/// <param name="Mode">Target SELinux mode of the switch. <para>切换的目标 SELinux 模式。</para></param>
/// <param name="OriginalCommandLine">Command line as read from the boot header. <para>从 boot 头部读取的原始命令行。</para></param>
/// <param name="UpdatedCommandLine">Command line after token removal and mode token addition.
/// <para>移除旧标记并追加模式标记后的命令行。</para></param>
/// <param name="Limitations">Scope caveats attached to the plan, including that SELinux policy binaries
/// are unchanged and real-device behavior needs separate validation.
/// <para>方案附带的作用域限制说明，包括 SELinux 策略二进制文件未被改动、真机行为需单独验证。</para></param>
public sealed record SelinuxSwitchPlan(string BootPath, SelinuxSwitchMode Mode, string OriginalCommandLine,
    string UpdatedCommandLine, IReadOnlyList<string> Limitations);