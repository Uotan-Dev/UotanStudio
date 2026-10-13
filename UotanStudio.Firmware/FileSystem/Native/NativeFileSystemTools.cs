using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Native;

/// <summary>Runs installed filesystem tools without a shell; Windows callers may select a WSL distribution.
/// <para>以无 shell 方式运行已安装的文件系统工具；Windows 调用方可选择 WSL 发行版。</para></summary>
/// <param name="wslDistribution">
/// WSL distribution name passed to <c>wsl.exe -d</c> on Windows and ignored on other platforms. When it is <see langword="null"/> or whitespace on Windows, every tool invocation throws <see cref="PlatformNotSupportedException"/>.
/// <para>Windows 下传给 <c>wsl.exe -d</c> 的 WSL 发行版名称，其他平台忽略。Windows 下为 <see langword="null"/> 或空白时，每次工具调用都会抛出 <see cref="PlatformNotSupportedException"/>。</para>
/// </param>
public sealed class NativeFileSystemTools(string? wslDistribution = null)
{
    private const int MaximumOutputChars = 16 * 1024;

    /// <summary>
    /// Translates a host path into the form the native tools see.
    /// <para>把主机路径转换为本机工具可见的形式。</para>
    /// </summary>
    /// <param name="path">Host path resolved with <c>Path.GetFullPath</c> before translation.
    /// <para>转换前先经 <c>Path.GetFullPath</c> 解析的主机路径。</para></param>
    /// <param name="cancellationToken">Cancels the <c>wslpath</c> process and kills its tree.
    /// <para>取消 <c>wslpath</c> 进程并终止其进程树。</para></param>
    /// <returns>The absolute path as seen by tools running in the WSL distribution.
    /// <para>在 WSL 发行版中运行的工具所见的绝对路径。</para></returns>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="InvalidDataException"><c>wslpath</c> exited with a non-zero code.
    /// <para><c>wslpath</c> 以非零退出码结束。</para></exception>
    /// <exception cref="IOException">The process could not start, or <c>wslpath</c> returned a path that does not begin with <c>/</c>.
    /// <para>无法启动进程，或 <c>wslpath</c> 返回了不以 <c>/</c> 开头的路径。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public string PathForTool(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        if (string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var result = RunWslPathSync(fullPath.Replace('\\', '/'), cancellationToken);
        RequireSuccess("wslpath", result);
        var translated = result.Output.Trim();
        if (!translated.StartsWith('/'))
        {
            throw new IOException("WSL did not return an absolute path.");
        }

        return translated;
    }

    /// <summary>
    /// Translates a host path into the form the native tools see: plain full-path resolution outside Windows, an awaited <c>wslpath</c> run through <see cref="RunAsync"/> inside the configured WSL distribution on Windows.
    /// <para>将主机路径转换为本机工具可见的形式：非 Windows 平台直接解析完整路径；Windows 在已配置的 WSL 发行版内经由 <see cref="RunAsync"/> 等待运行 <c>wslpath</c>。</para>
    /// </summary>
    /// <param name="path">Host path resolved with <c>Path.GetFullPath</c> before translation.
    /// <para>转换前先经 <c>Path.GetFullPath</c> 解析的主机路径。</para></param>
    /// <param name="cancellationToken">Cancels the <c>wslpath</c> process and kills its tree.
    /// <para>取消 <c>wslpath</c> 进程并终止其进程树。</para></param>
    /// <returns>A task carrying the absolute path as seen by tools running in the WSL distribution.
    /// <para>携带在 WSL 发行版中运行的工具所见绝对路径的任务。</para></returns>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="InvalidDataException"><c>wslpath</c> exited with a non-zero code.
    /// <para><c>wslpath</c> 以非零退出码结束。</para></exception>
    /// <exception cref="IOException">The process could not start, or <c>wslpath</c> returned a path that does not begin with <c>/</c>.
    /// <para>无法启动进程，或 <c>wslpath</c> 返回了不以 <c>/</c> 开头的路径。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<string> PathForToolAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        if (string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var result = await RunAsync("wslpath", [fullPath.Replace('\\', '/')], cancellationToken).ConfigureAwait(false);
        RequireSuccess("wslpath", result);
        var translated = result.Output.Trim();
        if (!translated.StartsWith('/'))
        {
            throw new IOException("WSL did not return an absolute path.");
        }

        return translated;
    }

    /// <summary>
    /// Runs <c>wslpath</c> once as a synchronous process: stderr is drained through the event API so a full error pipe cannot deadlock the synchronous stdout read, cancellation kills the process tree, and the merged capture stays bounded at <see cref="MaximumOutputChars"/> characters.
    /// <para>以同步进程方式运行一次 <c>wslpath</c>：stderr 经事件 API 排空，错误管道写满也不会让同步的 stdout 读取死锁；取消时终止进程树，合并捕获上限为 <see cref="MaximumOutputChars"/> 字符。</para>
    /// </summary>
    private NativeToolResult RunWslPathSync(string toolPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add(wslDistribution!);
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wslpath");
        info.ArgumentList.Add(toolPath);

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new IOException("Could not start wslpath.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (output)
            {
                var remaining = MaximumOutputChars - output.Length;
                if (remaining > 0)
                {
                    output.Append(args.Data, 0, Math.Min(args.Data.Length, remaining));
                    output.Append('\n');
                }
            }
        };
        process.BeginErrorReadLine();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        lock (output)
        {
            var remaining = MaximumOutputChars - output.Length;
            if (remaining > 0)
            {
                output.Append(stdout, 0, Math.Min(stdout.Length, remaining));
            }
        }

        return new NativeToolResult(process.ExitCode, output.ToString());
    }

    /// <summary>
    /// Starts one tool process without a shell and captures its merged output.
    /// <para>以无 shell 方式启动一个工具进程并捕获其合并输出。</para>
    /// </summary>
    /// <param name="tool">Executable name started directly on Linux, or the command run inside the WSL distribution on Windows.
    /// <para>Linux 下直接启动的可执行文件名；Windows 下为在 WSL 发行版内执行的命令。</para></param>
    /// <param name="arguments">Argument strings passed verbatim, one list entry per argument, never re-parsed by a shell.
    /// <para>逐字传递的参数列表，每项一个参数，绝不经过 shell 重新解析。</para></param>
    /// <param name="cancellationToken">Cancels the run; the process tree is killed and awaited before the exception surfaces.
    /// <para>取消运行；先终止并等待进程树退出，异常才会抛出。</para></param>
    /// <returns>A task carrying the exit code and the bounded merged output.
    /// <para>携带退出码与有界合并输出的任务。</para></returns>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="IOException">The process could not be started.
    /// <para>无法启动进程。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled during startup, output capture, or exit wait.
    /// <para>在启动、输出捕获或等待退出期间令牌被取消。</para></exception>
    public async Task<NativeToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "wsl.exe" : tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(wslDistribution!);
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(tool);
        }
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new IOException($"Could not start {tool}.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var stdout = CaptureAsync(process.StandardOutput, output, cancellationToken);
        var stderr = CaptureAsync(process.StandardError, output, cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new NativeToolResult(process.ExitCode, output.ToString());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Throws when a tool exited with a non-zero code; a zero exit code returns silently.
    /// <para>工具以非零退出码结束时抛出异常；退出码为 <c>0</c> 时静默返回。</para>
    /// </summary>
    /// <param name="tool">Tool name embedded in the failure message.
    /// <para>嵌入失败信息的工具名。</para></param>
    /// <param name="result">Result whose <see cref="NativeToolResult.ExitCode"/> is tested.
    /// <para>被测 <see cref="NativeToolResult.ExitCode"/> 的运行结果。</para></param>
    /// <exception cref="InvalidDataException">The exit code is non-zero; the message embeds the captured output.
    /// <para>退出码非零；消息内嵌已捕获的输出。</para></exception>
    public static void RequireSuccess(string tool, NativeToolResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"{tool} exited with code {result.ExitCode}: {result.Output}");
        }
    }

    /// <summary>
    /// Builds an ext4 image from a staging directory with <c>make_ext4fs</c>.
    /// <para>使用 <c>make_ext4fs</c> 从暂存目录构建 ext4 镜像。</para>
    /// </summary>
    /// <param name="sourceDir">Staging directory tree loaded into the image; must exist.
    /// <para>要装入镜像的暂存目录树；必须存在。</para></param>
    /// <param name="outputPath">Destination image path; an existing file is replaced only after all checks pass.
    /// <para>目标镜像路径；仅在全部检查通过后才会覆盖已有文件。</para></param>
    /// <param name="options">Argument mapping for mount point, timestamp, block size, sidecar files, label and partition size.
    /// <para>挂载点、时间戳、块大小、附属文件、标签与分区大小的参数映射。</para></param>
    /// <param name="cancellationToken">Cancels path mapping and the tool run; the destination stays untouched.
    /// <para>取消路径映射与工具运行；目标文件保持不变。</para></param>
    /// <returns>The <see cref="NativeToolResult"/> of the <c>make_ext4fs</c> run once the image has been published.
    /// <para>镜像发布后返回 <c>make_ext4fs</c> 的 <see cref="NativeToolResult"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDir"/> does not exist.
    /// <para><paramref name="sourceDir"/> 不存在。</para></exception>
    /// <exception cref="FileNotFoundException">A configured <c>fs_config</c> or <c>file_contexts</c> sidecar is missing.
    /// <para>配置的 <c>fs_config</c> 或 <c>file_contexts</c> 附属文件缺失。</para></exception>
    /// <exception cref="ArgumentException">The mount point or label is unsafe, or <paramref name="outputPath"/> lies inside <paramref name="sourceDir"/>.
    /// <para>挂载点或标签不安全，或 <paramref name="outputPath"/> 位于 <paramref name="sourceDir"/> 之内。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block size or a bounded numeric option is outside its supported range.
    /// <para>块大小或受限数值选项超出支持范围。</para></exception>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="InvalidDataException"><c>make_ext4fs</c> failed via <see cref="RequireSuccess"/> or the image lacks the EXT4 magic.
    /// <para><c>make_ext4fs</c> 经 <see cref="RequireSuccess"/> 判定失败，或镜像缺少 EXT4 魔数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<NativeToolResult> MakeExt4fsAsync(string sourceDir, string outputPath, MakeExt4fsOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateAndroidMountPoint(options.MountPoint);
        ValidateOptionalLabel(options.Label);
        if (options.BlockSize is not (1024 or 2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "make_ext4fs block size must be 1024, 2048 or 4096.");
        }
        if (options.TimestampSeconds is < 0 or > 4_102_445_945)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "make_ext4fs timestamp must be between 0 and year 2100.");
        }
        if (options.PartitionSizeBytes is < 65_536 or > 1_099_511_627_776)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "make_ext4fs partition size must be between 64 KiB and 1 TiB.");
        }

        var source = Path.GetFullPath(sourceDir);
        var destination = Path.GetFullPath(outputPath);
        using var sidecars = NativeMetadataSidecars.TryCreate(source, cancellationToken);
        var fsConfigPath = sidecars?.FsConfigPath ?? options.FsConfigPath;
        var fileContextsPath = sidecars?.FileContextsPath ?? options.FileContextsPath;
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }
        if (fsConfigPath is { } fsConfig && !File.Exists(fsConfig))
        {
            throw new FileNotFoundException("Android fs_config file was not found.", fsConfig);
        }
        if (fileContextsPath is { } fileContexts && !File.Exists(fileContexts))
        {
            throw new FileNotFoundException("Android file_contexts file was not found.", fileContexts);
        }
        RejectOutputInsideSource(source, destination);

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            List<string> arguments = ["-l"];
            arguments.Add("-b");
            arguments.Add(options.BlockSize.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-a");
            arguments.Add(options.MountPoint);
            if (options.TimestampSeconds is { } timestamp)
            {
                arguments.Add("-T");
                arguments.Add(timestamp.ToString(CultureInfo.InvariantCulture));
            }
            if (fsConfigPath is not null)
            {
                arguments.Add("-f");
                arguments.Add(await PathForToolAsync(fsConfigPath, cancellationToken).ConfigureAwait(false));
            }
            if (fileContextsPath is not null)
            {
                arguments.Add("-S");
                arguments.Add(await PathForToolAsync(fileContextsPath, cancellationToken).ConfigureAwait(false));
            }
            if (options.Label is { } label)
            {
                arguments.Add("-L");
                arguments.Add(label);
            }
            if (options.PartitionSizeBytes is { } partitionSize)
            {
                arguments.Add("-s");
                arguments.Add(partitionSize.ToString(CultureInfo.InvariantCulture));
            }
            arguments.Add(await PathForToolAsync(temporary, cancellationToken).ConfigureAwait(false));
            arguments.Add(await PathForToolAsync(source, cancellationToken).ConfigureAwait(false));

            var result = await RunAsync("make_ext4fs", arguments, cancellationToken).ConfigureAwait(false);
            RequireSuccess("make_ext4fs", result);
            await RequireExt4Superblock(temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Builds an ext4 image from a staging directory with the Android 9.0+ <c>mke2fs</c> and <c>e2fsdroid</c> pipeline.
    /// <para>使用 Android 9.0+ 的 <c>mke2fs</c> 与 <c>e2fsdroid</c> 两阶段流水线从暂存目录构建 ext4 镜像。</para>
    /// </summary>
    /// <param name="sourceDir">Staging directory tree loaded by <c>e2fsdroid</c>; must exist.
    /// <para>由 <c>e2fsdroid</c> 装载的暂存目录树；必须存在。</para></param>
    /// <param name="outputPath">Destination image path; an existing file is replaced only after all checks pass.
    /// <para>目标镜像路径；仅在全部检查通过后才会覆盖已有文件。</para></param>
    /// <param name="options">Image capacity, mount point, sidecar selection and <c>mke2fs</c>/<c>e2fsdroid</c> flags.
    /// <para>镜像容量、挂载点、附属文件选择以及 <c>mke2fs</c>/<c>e2fsdroid</c> 开关。</para></param>
    /// <param name="cancellationToken">Cancels path mapping, both tool runs and the publish step.
    /// <para>取消路径映射、两次工具运行以及发布步骤。</para></param>
    /// <returns>The <see cref="NativeToolResult"/> of the final <c>e2fsdroid</c> run.
    /// <para>最终 <c>e2fsdroid</c> 运行的 <see cref="NativeToolResult"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDir"/> does not exist.
    /// <para><paramref name="sourceDir"/> 不存在。</para></exception>
    /// <exception cref="FileNotFoundException">A configured <c>file_contexts</c> sidecar is missing.
    /// <para>配置的 <c>file_contexts</c> 附属文件缺失。</para></exception>
    /// <exception cref="ArgumentException">The mount point, label or an <c>mke2fs</c> name is unsafe, or <paramref name="outputPath"/> lies inside <paramref name="sourceDir"/>.
    /// <para>挂载点、标签或 <c>mke2fs</c> 名称不安全，或 <paramref name="outputPath"/> 位于 <paramref name="sourceDir"/> 之内。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The capacity, block size, SDK version or timestamp is outside its supported range.
    /// <para>容量、块大小、SDK 版本或时间戳超出支持范围。</para></exception>
    /// <exception cref="InvalidDataException">The <c>-e</c> fs_config value is neither an existing file nor a built-in token, or a tool failed via <see cref="RequireSuccess"/>.
    /// <para><c>-e</c> 的 fs_config 既不是现存文件也不是内置令牌，或工具经 <see cref="RequireSuccess"/> 判定失败。</para></exception>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<NativeToolResult> E2fsDroidAsync(string sourceDir, string outputPath, E2fsDroidOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateAndroidMountPoint(options.MountPoint);
        ValidateOptionalLabel(options.Label);
        ValidateMkfsName(options.MkfsType, nameof(options));
        ValidateMkfsName(options.MkfsProfile, nameof(options));
        if (options.BlockSize is not (1024 or 2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "e2fsdroid block size must be 1024, 2048 or 4096.");
        }
        if (options.ImageSizeBytes is < 65_536 or > 1_099_511_627_776 || options.ImageSizeBytes % options.BlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "e2fsdroid image capacity must be between 64 KiB and 1 TiB and a multiple of the block size.");
        }
        if (options.SdkVersion is not null and (< 1 or > 10_000))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "e2fsdroid SDK version must be between 1 and 10000.");
        }
        if (options.FixedTimestampSeconds is < 0 or > 4_102_445_945)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "e2fsdroid timestamp must be between 0 and year 2100.");
        }
        var source = Path.GetFullPath(sourceDir);
        var destination = Path.GetFullPath(outputPath);
        var fsConfigIsBuiltin = options.FsConfig is "A" or "B" or "T" or "U" or "all_a" or "system";
        using var sidecars = NativeMetadataSidecars.TryCreate(source, cancellationToken);
        var fsConfigPath = !fsConfigIsBuiltin && sidecars?.FsConfigPath is { } materializedFsConfig
            ? materializedFsConfig
            : options.FsConfig;
        var fileContextsPath = sidecars?.FileContextsPath ?? options.FileContextsPath;
        if (!fsConfigIsBuiltin && (fsConfigPath.Length == 0 || !File.Exists(fsConfigPath)))
        {
            throw new InvalidDataException("The e2fsdroid fs_config value is neither an existing file nor a built-in token.");
        }
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }
        if (fileContextsPath is { } fileContexts && !File.Exists(fileContexts))
        {
            throw new FileNotFoundException("Android file_contexts file was not found.", fileContexts);
        }
        RejectOutputInsideSource(source, destination);
        var fsConfigToolPath = fsConfigIsBuiltin ? options.FsConfig : await PathForToolAsync(fsConfigPath, cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var image = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                image.SetLength(options.ImageSizeBytes);
            }

            var imagePath = await PathForToolAsync(temporary, cancellationToken).ConfigureAwait(false);
            var mkfsArguments = new List<string>
            {
                "-F", "-q", "-t", options.MkfsType, "-T", options.MkfsProfile,
                "-b", options.BlockSize.ToString(CultureInfo.InvariantCulture)
            };
            if (options.Label is { } label)
            {
                mkfsArguments.Add("-L");
                mkfsArguments.Add(label);
            }
            mkfsArguments.Add(imagePath);
            RequireSuccess("mke2fs", await RunAsync("mke2fs", mkfsArguments, cancellationToken).ConfigureAwait(false));

            List<string> droidArguments = ["-e", fsConfigToolPath];
            if (fileContextsPath is not null)
            {
                droidArguments.Add("-S");
                droidArguments.Add(await PathForToolAsync(fileContextsPath, cancellationToken).ConfigureAwait(false));
            }
            droidArguments.Add("-a");
            droidArguments.Add(options.MountPoint);
            if (options.SdkVersion is { } sdkVersion)
            {
                droidArguments.Add("-t");
                droidArguments.Add(sdkVersion.ToString(CultureInfo.InvariantCulture));
            }
            if (options.FixedTimestampSeconds is { } timestamp)
            {
                droidArguments.Add("-T");
                droidArguments.Add(timestamp.ToString(CultureInfo.InvariantCulture));
            }
            if (options.Unpartitioned)
            {
                droidArguments.Add("-u");
            }
            droidArguments.Add(await PathForToolAsync(source, cancellationToken).ConfigureAwait(false));
            droidArguments.Add(imagePath);

            var result = await RunAsync("e2fsdroid", droidArguments, cancellationToken).ConfigureAwait(false);
            RequireSuccess("e2fsdroid", result);
            await RequireExt4Superblock(temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateAndroidMountPoint(string mountPoint)
    {
        if (!mountPoint.StartsWith('/') || mountPoint.Contains("..", StringComparison.Ordinal)
            || mountPoint.Any(char.IsControl) || mountPoint.Length > 256)
        {
            throw new ArgumentException("Mount point must be an absolute Android path without control characters.", nameof(mountPoint));
        }
    }

    private static void ValidateOptionalLabel(string? label)
    {
        if (label is not null && (label.Length == 0 || label.Length > 128 || label.Any(char.IsControl)))
        {
            throw new ArgumentException("The image label must be 1 to 128 characters without control characters.", nameof(label));
        }
    }

    private static void ValidateMkfsName(string value, string parameterName)
    {
        if (value.Length is 0 or > 32 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new ArgumentException("mke2fs type and profile names are limited to 32 ASCII letters, digits, '_' or '-'.", parameterName);
        }
    }

    private static void RejectOutputInsideSource(string source, string destination)
    {
        if (destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The output must not be inside the source directory.", nameof(destination));
        }
    }

    private static async Task RequireExt4Superblock(string imagePath, CancellationToken cancellationToken)
    {
        await using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (image.Length < 1082)
        {
            throw new InvalidDataException("The produced image is too small to contain an EXT4 superblock.");
        }
        image.Position = 1024 + 56;
        var magic = new byte[2];
        await image.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (magic[0] != 0x53 || magic[1] != 0xEF)
        {
            throw new InvalidDataException("The produced image does not contain a raw EXT4 superblock.");
        }
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            lock (output)
            {
                if (output.Length < MaximumOutputChars)
                {
                    output.Append(buffer, 0, Math.Min(count, MaximumOutputChars - output.Length));
                }
            }
        }
    }
}

/// <summary>Outcome of one native tool run: the process exit code plus the bounded merged output.
/// <para>一次本机工具运行的结果：进程退出码与有界合并输出。</para></summary>
/// <param name="ExitCode">Process exit code; <c>0</c> means success.
/// <para>进程退出码；<c>0</c> 表示成功。</para></param>
/// <param name="Output">stdout and stderr merged into one string, truncated at 16384 characters.
/// <para>stdout 与 stderr 的合并字符串，截断于 16384 字符。</para></param>
public readonly record struct NativeToolResult(int ExitCode, string Output);

/// <summary>
/// Materialises the structured preservation payload of an extraction directory into the temporary flat
/// <c>fs_config</c> and <c>file_contexts</c> files native tools require, because their command-line contract is
/// "a file already exists at this path". Rows follow the strict AOSP dialect shared with the extraction-time
/// export: space-delimited fields, four-digit octal mode, a literal <c>0</c> capabilities column, LF endings and
/// no UTF BOM, and the grammar cannot carry a path or label holding whitespace or an inode type with no type
/// column, so those rows are skipped. The published files are written through atomic publication and removed when
/// the instance is disposed, which keeps a tool run from ever reading a sidecar left behind by an earlier
/// extraction. A directory whose metadata carries no payload yields no instance, so an externally supplied
/// hand-authored sidecar keeps working for a directory build.
/// <para>把解包目录的结构化保留负载具象化为本机工具所需的临时扁平 <c>fs_config</c> 与 <c>file_contexts</c> 文件，
/// 因为它们的命令行约定是“该路径上已存在文件”。行与解包期导出共用严格的 AOSP 方言：以空格分隔字段、四位八进制
/// 模式、字面 <c>0</c> 的 capabilities 列、LF 行尾且不带 UTF BOM；该语法无法承载含空白的路径或标签，也没有文件
/// 类型列可供特殊节点使用，因此这些行会被跳过。发布出的文件经原子发布写出，实例释放时移除，使工具运行绝不会读到
/// 上一次解包遗留的附属文件。元数据不携带负载的目录不会产生实例，因此目录构建仍可继续使用外部提供的、人工编写的
/// 附属文件。</para>
/// </summary>
internal sealed class NativeMetadataSidecars : IDisposable
{
    /// <summary>
    /// BOM-free UTF-8 for both materialised files, matching the extraction-time export so a native reader never
    /// sees <c>EF BB BF</c> on line 1.
    /// <para>两个具象化文件使用的无 BOM UTF-8，与解包期导出一致，使本机读取器不会在第一行看到 <c>EF BB BF</c>。</para>
    /// </summary>
    private static readonly UTF8Encoding SidecarEncoding = new(false);

    private string? _directory;

    private NativeMetadataSidecars(string directory, string fsConfigPath, string fileContextsPath)
    {
        _directory = directory;
        FsConfigPath = fsConfigPath;
        FileContextsPath = fileContextsPath;
    }

    /// <summary>Temporary <c>fs_config</c> path handed to the tool.
    /// <para>交给工具的临时 <c>fs_config</c> 路径。</para></summary>
    /// <value>An existing file path inside the temporary directory. <para>临时目录内已存在的文件路径。</para></value>
    internal string FsConfigPath { get; }

    /// <summary>Temporary <c>file_contexts</c> path handed to the tool.
    /// <para>交给工具的临时 <c>file_contexts</c> 路径。</para></summary>
    /// <value>An existing file path inside the temporary directory. <para>临时目录内已存在的文件路径。</para></value>
    internal string FileContextsPath { get; }

    /// <summary>
    /// Writes both sidecars for a preserved extraction directory.
    /// <para>为受保留的解包目录写出两个附属文件。</para>
    /// </summary>
    /// <param name="sourceDirectory">Directory being packed, read for its metadata and payload.
    /// <para>正在打包的目录，用于读取其元数据与负载。</para></param>
    /// <param name="cancellationToken">Cancels the sidecar writes. <para>取消附属文件写出。</para></param>
    /// <returns>The materialised paths, or <see langword="null"/> when the directory carries no structured
    /// preservation payload. <para>具象化后的路径；目录不携带结构化保留负载时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The temporary directory or a staged sidecar cannot be written.
    /// <para>无法写出临时目录或暂存附属文件。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static NativeMetadataSidecars? TryCreate(string sourceDirectory, CancellationToken cancellationToken)
    {
        var payload = FileSystemPreservation.ReadPayload(RepackMetadata.Read(sourceDirectory));
        if (payload is null)
        {
            return null;
        }

        var directory = TempFileHelper.CreateScratchPath("uotan-native-sidecar", ".d");
        Directory.CreateDirectory(directory);
        try
        {
            var fsConfigPath = WriteSidecar(directory, "fs_config", BuildFsConfigRows(payload), cancellationToken);
            var fileContextsPath = WriteSidecar(directory, "file_contexts", BuildFileContextsRows(payload), cancellationToken);
            return new NativeMetadataSidecars(directory, fsConfigPath, fileContextsPath);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    /// <summary>Removes the temporary directory holding both sidecars; a delete that fails leaves the files for
    /// the system temporary-directory sweep.
    /// <para>移除持有两个附属文件的临时目录；删除失败时把文件留给系统临时目录清理。</para></summary>
    public void Dispose()
    {
        if (_directory is not { } directory)
        {
            return;
        }

        _directory = null;
        TryDeleteDirectory(directory);
    }

    private static string WriteSidecar(string directory, string fileName, List<string> rows,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(row).Append('\n');
        }

        var target = Path.Combine(directory, fileName);
        var staged = TempFileHelper.CreateAdjacentPath(target);
        try
        {
            File.WriteAllText(staged, builder.ToString(), SidecarEncoding);
            AtomicPublish.Publish(staged, target);
            return target;
        }
        finally
        {
            TempFileHelper.SafeDelete(staged);
        }
    }

    private static List<string> BuildFsConfigRows(FileSystemPreservationData payload)
    {
        Dictionary<string, ushort> typeBits = new(StringComparer.Ordinal);
        foreach (var entry in payload.ParsedEntries)
        {
            typeBits[FileSystemEditPlan.Normalize(entry.Path)] = (ushort)(entry.Mode & InodeTypeBits.FormatMask);
        }

        List<string> rows = [];
        foreach (var (key, value) in payload.FsConfig.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var imagePath = key.Length == 0 ? "/" : "/" + key;
            if (imagePath.Any(char.IsWhiteSpace))
            {
                continue;
            }

            if (typeBits.TryGetValue(key, out var type)
                && type is not (0 or InodeTypeBits.RegularFile or InodeTypeBits.Directory))
            {
                continue;
            }

            rows.Add($"{imagePath} {value.Uid.ToString(CultureInfo.InvariantCulture)} " +
                $"{value.Gid.ToString(CultureInfo.InvariantCulture)} {FsConfigModes.Format(value.Mode)} 0");
        }

        return rows;
    }

    private static List<string> BuildFileContextsRows(FileSystemPreservationData payload)
    {
        List<string> rows = [];
        foreach (var (key, label) in payload.FileContexts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var imagePath = key.Length == 0 ? "/" : "/" + key;
            if (imagePath.Any(char.IsWhiteSpace) || label.Any(char.IsWhiteSpace))
            {
                continue;
            }

            rows.Add($"{imagePath} {label}");
        }

        return rows;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Argument mapping for one <c>make_ext4fs</c> run; each optional property translates to exactly one tool flag
/// and <c>-l</c> is always emitted. The flag semantics follow the tool's own convention and are only verified
/// against an installed binary, never emulated here.
/// <para>一次 <c>make_ext4fs</c> 运行的参数映射；每个可选属性恰好对应一个工具开关，且 <c>-l</c> 始终传入。
/// 开关语义以工具自身约定为准，只在安装了真实二进制时才可验证，本层不做任何模拟。</para>
/// </summary>
public sealed record MakeExt4fsOptions
{
    /// <summary>Android mount point passed via <c>-a</c>; absolute, no <c>..</c> segment, at most 256 characters. Defaults to <c>/system</c>.
    /// <para>经 <c>-a</c> 传入的 Android 挂载点；须为绝对路径、不含 <c>..</c> 段且不超过 256 个字符，默认 <c>/system</c>。</para></summary>
    public string MountPoint { get; init; } = "/system";

    /// <summary>Optional Android <c>fs_config</c> override file passed via <c>-f</c>; the file must exist.
    /// <para>可选的 Android <c>fs_config</c> 覆盖文件，经 <c>-f</c> 传入；文件必须存在。</para></summary>
    public string? FsConfigPath { get; init; }

    /// <summary>Optional SELinux <c>file_contexts</c> file passed via <c>-S</c>; the file must exist.
    /// <para>可选的 SELinux <c>file_contexts</c> 文件，经 <c>-S</c> 传入；文件必须存在。</para></summary>
    public string? FileContextsPath { get; init; }

    /// <summary>Optional filesystem label passed via <c>-L</c>; 1 to 128 characters without control characters.
    /// <para>可选的文件系统标签，经 <c>-L</c> 传入；1 到 128 个字符且不含控制字符。</para></summary>
    public string? Label { get; init; }

    /// <summary>Optional fixed UNIX timestamp in seconds passed via <c>-T</c>; 0 to 4102445945 (year 2100).
    /// <para>可选的固定 UNIX 秒级时间戳，经 <c>-T</c> 传入；范围 0 到 4102445945（2100 年）。</para></summary>
    public long? TimestampSeconds { get; init; }

    /// <summary>Block size passed via <c>-b</c>; only 1024, 2048 and 4096 are accepted. Defaults to 4096.
    /// <para>经 <c>-b</c> 传入的块大小；仅接受 1024、2048、4096，默认 4096。</para></summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>Optional partition size in bytes passed via <c>-s</c>; 64 KiB to 1 TiB.
    /// <para>可选的分区大小（字节），经 <c>-s</c> 传入；范围 64 KiB 到 1 TiB。</para></summary>
    public long? PartitionSizeBytes { get; init; }
}

/// <summary>
/// Argument mapping for one <c>mke2fs</c> plus <c>e2fsdroid</c> stage pair. The capacity bounds every later
/// offset check; the <c>FsConfig</c> value is either one of the <c>e2fsdroid</c> built-in tokens
/// (<c>A</c>, <c>B</c>, <c>T</c>, <c>U</c>, <c>all_a</c>, <c>system</c>) or a path to an existing file.
/// <para>一次 <c>mke2fs</c> 加 <c>e2fsdroid</c> 阶段组合的参数映射。容量值约束后续所有偏移检查；
/// <c>FsConfig</c> 取 <c>e2fsdroid</c> 内置令牌（<c>A</c>、<c>B</c>、<c>T</c>、<c>U</c>、<c>all_a</c>、<c>system</c>）
/// 之一，或指向现存文件的路径。</para>
/// </summary>
public sealed record E2fsDroidOptions
{
    /// <summary>Fixed image capacity in bytes; 64 KiB to 1 TiB and a multiple of <see cref="BlockSize"/>.
    /// <para>固定镜像容量（字节）；64 KiB 到 1 TiB，且须为 <see cref="BlockSize"/> 的整数倍。</para></summary>
    public required long ImageSizeBytes { get; init; }

    /// <summary>Android mount point passed to <c>e2fsdroid -a</c>; absolute, no <c>..</c> segment. Defaults to <c>/system</c>.
    /// <para>传给 <c>e2fsdroid -a</c> 的 Android 挂载点；须为绝对路径且不含 <c>..</c> 段，默认 <c>/system</c>。</para></summary>
    public string MountPoint { get; init; } = "/system";

    /// <summary><c>e2fsdroid -e</c> value: a built-in token or a path to an existing <c>fs_config</c> file. Defaults to <c>A</c>.
    /// <para><c>e2fsdroid -e</c> 的取值：内置令牌，或现存 <c>fs_config</c> 文件路径，默认 <c>A</c>。</para></summary>
    public string FsConfig { get; init; } = "A";

    /// <summary>Optional SELinux <c>file_contexts</c> file passed via <c>e2fsdroid -S</c>; the file must exist.
    /// <para>可选的 SELinux <c>file_contexts</c> 文件，经 <c>e2fsdroid -S</c> 传入；文件必须存在。</para></summary>
    public string? FileContextsPath { get; init; }

    /// <summary>Optional target SDK version passed via <c>e2fsdroid -t</c>; 1 to 10000.
    /// <para>可选的目标 SDK 版本，经 <c>e2fsdroid -t</c> 传入；范围 1 到 10000。</para></summary>
    public int? SdkVersion { get; init; }

    /// <summary>Optional fixed UNIX timestamp in seconds passed via <c>e2fsdroid -T</c>; 0 to 4102445945 (year 2100).
    /// <para>可选的固定 UNIX 秒级时间戳，经 <c>e2fsdroid -T</c> 传入；范围 0 到 4102445945（2100 年）。</para></summary>
    public long? FixedTimestampSeconds { get; init; }

    /// <summary>Filesystem type passed via <c>mke2fs -t</c>; up to 32 ASCII letters, digits, <c>_</c> or <c>-</c>. Defaults to <c>ext4</c>.
    /// <para>经 <c>mke2fs -t</c> 传入的文件系统类型；至多 32 个 ASCII 字母、数字、<c>_</c> 或 <c>-</c>，默认 <c>ext4</c>。</para></summary>
    public string MkfsType { get; init; } = "ext4";

    /// <summary>Mkfs profile passed via <c>mke2fs -T</c>; up to 32 ASCII letters, digits, <c>_</c> or <c>-</c>. Defaults to <c>android</c>.
    /// <para>经 <c>mke2fs -T</c> 传入的 mkfs 配置模板；至多 32 个 ASCII 字母、数字、<c>_</c> 或 <c>-</c>，默认 <c>android</c>。</para></summary>
    public string MkfsProfile { get; init; } = "android";

    /// <summary>Block size passed via <c>mke2fs -b</c>; only 1024, 2048 and 4096 are accepted. Defaults to 4096.
    /// <para>经 <c>mke2fs -b</c> 传入的块大小；仅接受 1024、2048、4096，默认 4096。</para></summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>Optional filesystem label passed via <c>mke2fs -L</c>; 1 to 128 characters without control characters.
    /// <para>可选的文件系统标签，经 <c>mke2fs -L</c> 传入；1 到 128 个字符且不含控制字符。</para></summary>
    public string? Label { get; init; }

    /// <summary>Adds <c>e2fsdroid -u</c> so the image is finalized as unpartitioned (dynamic-partition member).
    /// <para>置位则追加 <c>e2fsdroid -u</c>，把镜像按未分区（动态分区成员）方式收尾。</para></summary>
    public bool Unpartitioned { get; init; }
}