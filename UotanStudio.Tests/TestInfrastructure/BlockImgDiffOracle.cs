using System.Diagnostics;
namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Drives the upstream blockimgdiff oracle (the AOSP-derived Python
/// <c>blockimgdiff.py</c> plus <c>rangelib.py</c>) through the environment
/// variable <c>UOTAN_BLOCKIMGDIFF_ORACLE</c>, which must name a directory that
/// contains both modules. The class assembles a throwaway Python package from
/// those files plus the repository's own <c>pybsdiff.py</c> and
/// <c>driver.py</c> harnesses, runs the oracle's <c>BlockImageDiff.Compute</c>
/// pipeline on the pinned synthetic corpus, and can replay an
/// UotanStudio-generated dataset through <c>verify_uotan.py</c>. Python must be
/// reachable as <c>python</c> or <c>py</c> on PATH; the oracle normally shells
/// out to native <c>bsdiff</c>/<c>imgdiff</c> binaries that are not present on
/// Windows, so the driver substitutes the repository's pure-Python BSDIFF40
/// encoder while every transfer-list decision still comes from the unmodified
/// oracle code.
/// <para>通过环境变量 <c>UOTAN_BLOCKIMGDIFF_ORACLE</c>（须指向同时包含 AOSP 系
/// <c>blockimgdiff.py</c> 与 <c>rangelib.py</c> 的目录）驱动上游 oracle。本类把这些文件与仓库自带的
/// <c>pybsdiff.py</c>、<c>driver.py</c> 组装成一次性 Python 包，在固定合成语料上运行 oracle 的
/// <c>BlockImageDiff.Compute</c> 流水线，并可用 <c>verify_uotan.py</c> 回放 UotanStudio 生成的数据集。
/// 运行环境需在 PATH 中提供 <c>python</c> 或 <c>py</c>；oracle 本应调用原生
/// <c>bsdiff</c>/<c>imgdiff</c> 二进制，但 Windows 上并不存在，因此 driver 以仓库内纯 Python 的
/// BSDIFF40 编码器替代，传输列表的全部决策仍来自未修改的 oracle 代码。</para>
/// </summary>
public static class BlockImgDiffOracle
{
    /// <summary>Environment variable naming the directory that holds <c>blockimgdiff.py</c> and
    /// <c>rangelib.py</c>. <para>指定包含 <c>blockimgdiff.py</c> 与 <c>rangelib.py</c> 的目录的环境变量名。</para></summary>
    public const string DirectoryVariable = "UOTAN_BLOCKIMGDIFF_ORACLE";

    /// <summary>Skip reason surfaced by oracle-gated tests when the gate is closed.
    /// <para>oracle 门控测试在门未开启时给出的跳过原因。</para></summary>
    public const string MissingOracleMessage =
        "Set UOTAN_BLOCKIMGDIFF_ORACLE to a directory containing blockimgdiff.py and rangelib.py, " +
        "and keep a Python interpreter on PATH, to regenerate and re-verify the blockimgdiff interop fixtures.";

    /// <summary>Whether the configured oracle directory and a runnable Python interpreter are both present.
    /// <para>配置的 oracle 目录与可运行的 Python 解释器是否同时具备。</para></summary>
    /// <value>
    /// <see langword="true"/> when <see cref="DirectoryVariable"/> resolves to a directory containing both
    /// oracle modules and <c>python --version</c> (or <c>py -3 --version</c>) starts; otherwise
    /// <see langword="false"/>.
    /// <para>当环境变量解析到包含两个 oracle 模块的目录，且 <c>python --version</c>（或 <c>py -3 --version</c>）
    /// 能启动时为 <see langword="true"/>，否则为 <see langword="false"/>。</para>
    /// </value>
    public static bool IsAvailable => ResolveOracleDirectory() is not null && TryResolvePython() is not null;

    /// <summary>Runs the oracle pipeline for the pinned synthetic corpus into
    /// <paramref name="outputDirectory"/>. <para>针对固定合成语料运行 oracle 流水线，输出到 <paramref name="outputDirectory"/>。</para></summary>
    /// <param name="outputDirectory">
    /// Directory receiving <c>system.transfer.list</c>, <c>system.new.dat</c>, <c>system.patch.dat</c>,
    /// <c>system.src.bin</c> and <c>system.tgt.bin</c>. <para>接收上述五个工件的目录。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the oracle process and kills it if it ignores the token.
    /// <para>取消 oracle 进程；进程无视取消时将其终止。</para></param>
    /// <exception cref="InvalidOperationException">
    /// The gate is closed (<see cref="IsAvailable"/> is <see langword="false"/>) or the oracle process failed.
    /// <para>门控未开启（<see cref="IsAvailable"/> 为 <see langword="false"/>）或 oracle 进程失败。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>已请求取消。</para></exception>
    public static void GenerateArtifacts(string outputDirectory, CancellationToken cancellationToken)
    {
        var packageRoot = BuildPackageDirectory();
        try
        {
            Run(packageRoot, $"-m imgoracle.driver \"{outputDirectory}\"", cancellationToken);
        }
        finally
        {
            TryDelete(packageRoot);
        }
    }

    /// <summary>Replays an UotanStudio-generated dataset through the repository's independent Python
    /// updater simulation. <para>用仓库自带的独立 Python 更新器模拟回放 UotanStudio 生成的数据集。</para></summary>
    /// <param name="artifactsDirectory">
    /// Directory holding <c>system.transfer.list</c>, <c>system.new.dat</c> and <c>system.patch.dat</c> as
    /// produced by <c>DatWriter</c>. <para>存放由 <c>DatWriter</c> 写出的三元组的目录。</para>
    /// </param>
    /// <param name="baseDirectory">
    /// Directory holding <c>system.src.bin</c> and <c>system.tgt.bin</c>. <para>存放源镜像与目标镜像的目录。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the verifier process. <para>取消校验进程。</para></param>
    /// <exception cref="InvalidOperationException">
    /// The gate is closed or the verifier reported a mismatch. <para>门控未开启，或校验器报告不一致。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>已请求取消。</para></exception>
    public static void VerifyArtifacts(string artifactsDirectory, string baseDirectory, CancellationToken cancellationToken)
    {
        var packageRoot = BuildPackageDirectory();
        try
        {
            var script = Path.Combine(packageRoot, "imgoracle", "verify_uotan.py");
            Run(Path.GetDirectoryName(script)!, $"\"{script}\" \"{artifactsDirectory}\" \"{baseDirectory}\"", cancellationToken);
        }
        finally
        {
            TryDelete(packageRoot);
        }
    }

    private static string BuildPackageDirectory()
    {
        if (ResolveOracleDirectory() is not { } oracleDirectory || TryResolvePython() is null)
        {
            throw new InvalidOperationException(MissingOracleMessage);
        }

        var root = Path.Combine(Path.GetTempPath(), "uotan-blockimgdiff-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "imgoracle");
        Directory.CreateDirectory(package);
        File.Create(Path.Combine(package, "__init__.py")).Dispose();
        foreach (var name in new[] { "blockimgdiff.py", "rangelib.py" })
        {
            File.Copy(Path.Combine(oracleDirectory, name), Path.Combine(package, name), overwrite: true);
        }

        var contentRoot = Path.GetDirectoryName(typeof(BlockImgDiffOracle).Assembly.Location)!;
        foreach (var name in new[] { "pybsdiff.py", "driver.py", "verify_uotan.py" })
        {
            var source = Path.Combine(contentRoot, "TestInfrastructure", "BlockImgDiff", name);
            File.Copy(source, Path.Combine(package, name), overwrite: true);
        }

        return root;
    }

    private static string? ResolveOracleDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured))
        {
            return null;
        }

        return File.Exists(Path.Combine(configured, "blockimgdiff.py")) && File.Exists(Path.Combine(configured, "rangelib.py"))
            ? configured
            : null;
    }

    private static string[]? TryResolvePython()
    {
        if (s_python is not null)
        {
            return s_python.Length == 0 ? null : s_python;
        }

        foreach (var candidate in new[] { new[] { "python" }, ["py", "-3"] })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate[0], string.Join(' ', candidate[1..]) + " --version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (process is null)
                {
                    continue;
                }

                process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                s_python = process.ExitCode == 0 ? candidate : [];
                if (s_python.Length != 0)
                {
                    return s_python;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                s_python = [];
            }
        }

        return s_python is { Length: > 0 } ? s_python : null;
    }

    private static void Run(string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        if (TryResolvePython() is not { } python)
        {
            throw new InvalidOperationException(MissingOracleMessage);
        }

        var startInfo = new ProcessStartInfo(python[0], string.Join(' ', python[1..]) + (arguments.Length == 0 ? "" : " " + arguments))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The Python process could not be started.");
        using (cancellationToken.Register(() => SafeKill(process)))
        {
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The blockimgdiff oracle failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
            }
        }
    }

    private static void SafeKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string[]? s_python;
}