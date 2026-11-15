using System.Text;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Decompile;
using UotanStudio.Firmware.AndroidMeta.Dex.Obfuscation;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Parses, disassembles, decompiles and rebuilds DEX files and DEX entries inside JAR/APK containers.
/// <para>解析、反汇编、反编译并重建 DEX 文件以及 JAR/APK 容器内的 DEX 条目。</para>
/// </summary>
public sealed class DexService
{
    private static readonly Lazy<DexService> LazyInstance = new(() => new DexService());

    /// <summary>
    /// Gets the DexService singleton instance.
    /// <para>获取 DexService 单例。</para>
    /// </summary>
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
    public static DexService Instance => LazyInstance.Value;

    private DexService() { }

    /// <summary>
    /// Parses a DEX file and returns its description of classes, methods and fields.
    /// <para>解析 DEX 文件并返回类、方法与字段等描述信息。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="entryName">
    /// The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para>
    /// </param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary carries the file description. <para>操作结果，摘要为文件描述。</para></returns>
    public Task<UotanStudio.Firmware.Core.FirmwareOpResult> ParseDexAsync(string path, string? entryName = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = LoadDex(path, entryName);
            var summary = dex.Describe();
            return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Ok(summary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Disassembles a DEX file into Smali text, written to the specified directory (defaults to the
    /// <c>&lt;name&gt;_dexout</c> folder next to the DEX file) as a <c>&lt;name&gt;.smali</c> file.
    /// <para>将 DEX 文件反汇编为 Smali 文本，写入指定目录（默认取 DEX 同目录的 <c>&lt;name&gt;_dexout</c> 文件夹）下的
    /// <c>&lt;name&gt;.smali</c> 文件。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="outputDirectory">The output directory; empty derives a folder beside the DEX file. <para>输出目录；为空时在 DEX 同目录推导。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry, ignored for plain files. <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary names the written file. <para>操作结果，摘要指明写出的文件。</para></returns>
    public async Task<UotanStudio.Firmware.Core.FirmwareOpResult> DisassembleDexAsync(string path, string? outputDirectory, string? entryName = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = LoadDex(path, entryName);
            var smali = Disassembler.DisassembleAll(dex);

            var name = Path.GetFileNameWithoutExtension(path);
            var safeName = string.IsNullOrEmpty(name) ? "dex" : name;
            outputDirectory ??= Path.Combine(
                Path.GetDirectoryName(path) ?? ".",
                safeName + "_dexout");
            Directory.CreateDirectory(outputDirectory);

            var smaliPath = Path.Combine(outputDirectory, safeName + ".smali");
            await File.WriteAllTextAsync(smaliPath, smali, Encoding.UTF8, ct).ConfigureAwait(false);

            var summary = $"Disassembled DEX to {smaliPath} ({Encoding.UTF8.GetByteCount(smali)} bytes)";
            return UotanStudio.Firmware.Core.FirmwareOpResult.Ok(summary, [smaliPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>Loads a DEX (or a container DEX entry) and rebuilds it to the specified output path; container sources are written back through the entry.
    /// <para>加载 DEX（或容器内 DEX 条目）并重建写入到指定输出路径；来源为容器时经条目写回。</para></summary>
    /// <param name="path">The DEX or container file path.<para>DEX 或容器文件路径。</para></param>
    /// <param name="outputPath">The output file path.<para>输出文件路径。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>Operation result whose summary names the written path.
    /// <para>操作结果，摘要指明写出路径。</para></returns>
    public Task<UotanStudio.Firmware.Core.FirmwareOpResult> RebuildDexAsync(string path, string outputPath, string? entryName = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (JarDexContainer.IsContainerPath(path))
            {
                var containerEntry = ResolveEntryName(path, entryName);
                JarDexContainer.WriteBack(path, containerEntry, LoadDex(path, containerEntry).ToArray(), ct);
                return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Ok(
                    $"Rebuilt {containerEntry} written back to {path}", [path]));
            }

            var dex = DexFile.Load(path);
            dex.Write(outputPath);
            return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Ok($"Rebuilt DEX written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Decompiles a DEX file into pseudo-Java source written to the given path or a default one beside the DEX file.
    /// <para>将 DEX 文件反编译为伪 Java 源码，写入给定路径或 DEX 同目录的默认路径。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="outputPath">The output source file path; empty derives a path beside the DEX file. <para>输出源码文件路径；为空时在 DEX 同目录推导。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry, ignored for plain files. <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para></param>
    /// <param name="options">Decompilation options; <see langword="null"/> uses the defaults. <para>反编译选项；空值使用默认设置。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary names the output path and byte count. <para>操作结果，摘要给出输出路径与字节数。</para></returns>
    public async Task<UotanStudio.Firmware.Core.FirmwareOpResult> DecompileDexAsync(string path, string? outputPath, string? entryName = null, DecompileOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = LoadDex(path, entryName);
            var java = LightweightDecompiler.DecompileAll(dex, options ?? DecompileOptions.Default);

            var name = Path.GetFileNameWithoutExtension(path);
            var safeName = string.IsNullOrEmpty(name) ? "dex" : name;
            outputPath ??= Path.Combine(
                Path.GetDirectoryName(path) ?? ".",
                safeName + ".java");

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(outputPath, java, Encoding.UTF8, ct).ConfigureAwait(false);

            var summary = $"Decompiled DEX to {outputPath} ({Encoding.UTF8.GetByteCount(java)} bytes)";
            return UotanStudio.Firmware.Core.FirmwareOpResult.Ok(summary, [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Repartitions the classes of a container's <c>classes*.dex</c> entries (or a plain DEX file) into
    /// however many dex images are required to stay inside the 16-bit identifier-table limits, writing
    /// the result to a new container (or part files beside a plain DEX). Existing v1 signature entries
    /// are stripped and the APK Signing Block is dropped because the rewrite invalidates both.
    /// <para>把容器的 <c>classes*.dex</c> 条目（或普通 DEX 文件）中的类重新分配为满足 16 位标识符表上限所需的
    /// 若干 dex 镜像，结果写入新容器（普通 DEX 则写到同目录分区文件）。重写会使 v1 签名条目与 APK 签名块同时失效，
    /// 因此两者都被剥离。</para>
    /// </summary>
    /// <param name="path">The container or plain DEX file path; the input file is never modified.
    /// <para>容器或普通 DEX 文件路径；输入文件不被修改。</para></param>
    /// <param name="outputPath">The output container path; required for container inputs. For a plain DEX the parts are written beside the input when this is empty.
    /// <para>输出容器路径；容器输入时必填。普通 DEX 输入时若为空则分区写在输入同目录。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary reports part count, class count and the re-signing reminder; the entry list names every written file.
    /// <para>操作结果，摘要报告分区数、类数与重签提醒；条目列表列出全部写出文件。</para></returns>
    public async Task<UotanStudio.Firmware.Core.FirmwareOpResult> RepartitionDexAsync(string path, string? outputPath, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<byte[]> parts;
            var written = new List<string>();
            if (JarDexContainer.IsContainerPath(path))
            {
                if (string.IsNullOrWhiteSpace(outputPath) || string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    return UotanStudio.Firmware.Core.FirmwareOpResult.Fail("Repartition output must be a new container path different from the input. 重新分配输出必须是不同于输入的新容器路径。");
                }
                var dexEntryNames = JarDexContainer.ListDexEntries(path);
                if (dexEntryNames.Count == 0)
                {
                    return UotanStudio.Firmware.Core.FirmwareOpResult.Fail("The container has no classes*.dex entry. 容器内没有 classes*.dex 条目。");
                }
                var sourceBytes = dexEntryNames
                    .Select(name => JarDexContainer.LoadEntry(path, name, ct))
                    .ToArray();
                parts = await Task.Run(() => DexRepartitioner.Rebalance(sourceBytes, ct), ct).ConfigureAwait(false);
                var entries = new List<(string Name, byte[] Content)>();
                for (var i = 0; i < parts.Count; i++)
                {
                    entries.Add((i == 0 ? "classes.dex" : $"classes{i + 1}.dex", parts[i]));
                }
                var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
                File.Copy(path, outputPath, overwrite: false);
                await Task.Run(() => JarDexContainer.RewriteDexEntries(outputPath, entries, stripSignatureEntries: true, ct), ct)
                    .ConfigureAwait(false);
                written.Add(outputPath);
            }
            else
            {
                var inputBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                parts = await Task.Run(() => DexRepartitioner.Split(inputBytes, ct), ct).ConfigureAwait(false);
                var stem = Path.GetFileNameWithoutExtension(path);
                var directory = string.IsNullOrWhiteSpace(outputPath)
                    ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? "."
                    : outputPath;
                Directory.CreateDirectory(directory);
                for (var i = 0; i < parts.Count; i++)
                {
                    var partPath = Path.Combine(directory, $"{stem}_part{i + 1}.dex");
                    await File.WriteAllBytesAsync(partPath, parts[i], ct).ConfigureAwait(false);
                    written.Add(partPath);
                }
            }

            var classCount = 0;
            foreach (var part in parts)
            {
                classCount += DexFile.Load(part).Classes.Count;
            }
            var summary = $"Repartitioned into {parts.Count} dex part(s) holding {classCount} class(es). "
                + "The rewrite dropped v1 signature entries and the APK Signing Block; re-sign the output before installing. "
                + $"Legacy-device main-dex lists are not computed. 已重新分配为 {parts.Count} 个 dex 分区，共 {classCount} 个类。"
                + "重写已剥离 v1 签名条目与 APK 签名块，安装前请重新签名。旧设备的 main-dex 列表不会被计算。";
            return UotanStudio.Firmware.Core.FirmwareOpResult.Ok(summary, written);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Strips debug information from a DEX file or container DEX entry and writes the rebuilt image
    /// (orphan strings from removed debug content leave the string pool).
    /// <para>剥离 DEX 文件或容器 DEX 条目的调试信息并写出重建镜像（仅被已删内容引用的字符串会离开字符串池）。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="outputPath">The output file path; for container entries it must differ from the input path.
    /// <para>输出文件路径；容器条目时必须与输入路径不同。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para></param>
    /// <param name="clearSourceFile">Also clears the per-class <c>.source</c> file name.
    /// <para>是否同时清除每类的 <c>.source</c> 文件名。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary names the output path and byte shrink.
    /// <para>操作结果，摘要给出输出路径与缩小量。</para></returns>
    public async Task<UotanStudio.Firmware.Core.FirmwareOpResult> StripDebugInfoAsync(string path, string outputPath, string? entryName = null, bool clearSourceFile = true, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var original = await ReadDexBytesAsync(path, entryName, ct).ConfigureAwait(false);
            var rebuilt = await Task.Run(() => DexPostProcessor.StripDebugInfoRebuilt(original, options: null, clearSourceFile), ct)
                .ConfigureAwait(false);
            await WriteDexBytesAsync(path, outputPath, entryName, rebuilt, ct).ConfigureAwait(false);
            var summary = $"Debug info stripped: {original.Length} → {rebuilt.Length} bytes, written to {outputPath}"
                + (clearSourceFile ? " (class source file names cleared)" : string.Empty)
                + $" 调试信息已剥离：{original.Length} → {rebuilt.Length} 字节，已写入 {outputPath}"
                + (clearSourceFile ? "（类源文件名已清除）" : string.Empty);
            return UotanStudio.Firmware.Core.FirmwareOpResult.Ok(summary, [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Repairs a damaged DEX file or container DEX entry via lenient re-parse and full rebuild, reporting every action taken.
    /// <para>经宽松重解析与全量重建修复受损的 DEX 文件或容器 DEX 条目，并报告每个动作。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="outputPath">The output file path; for container entries it must differ from the input path.
    /// <para>输出文件路径；容器条目时必须与输入路径不同。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary lists the repair actions.
    /// <para>操作结果，摘要列出修复动作。</para></returns>
    public async Task<UotanStudio.Firmware.Core.FirmwareOpResult> RepairDexAsync(string path, string outputPath, string? entryName = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var original = await ReadDexBytesAsync(path, entryName, ct).ConfigureAwait(false);
            DexRepairReport report = new();
            var repaired = await Task.Run(() => DexPostProcessor.Repair(original, out report), ct).ConfigureAwait(false);
            await WriteDexBytesAsync(path, outputPath, entryName, repaired, ct).ConfigureAwait(false);
            var actions = new List<string>();
            if (report.ChecksumMismatched) actions.Add("Adler-32 checksum recomputed (mismatched) / Adler-32 校验和不一致已重算");
            if (report.SignatureMismatched) actions.Add("SHA-1 signature recomputed (mismatched) / SHA-1 签名不一致已重算");
            if (report.TruncatedTrailingBytes) actions.Add($"{report.TruncatedByteCount} trailing bytes cut / 切除 {report.TruncatedByteCount} 字节尾部");
            if (actions.Count == 0) actions.Add("image parsed cleanly; tables, signature and checksum rebuilt / 镜像解析正常；表、签名与校验和已重建");
            return UotanStudio.Firmware.Core.FirmwareOpResult.Ok(
                $"Repaired DEX written to {outputPath}: {string.Join("; ", actions)} 已修复并写入 {outputPath}：{string.Join("；", actions)}",
                [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Runs the obfuscation counter-measures on a DEX file or container entry: user rules plus
    /// look-alike/invisible-name heuristics rename classes and members (ProGuard-style mapping
    /// returned in the summary), and optional encrypted-string recovery rewrites const-string
    /// operands.
    /// <para>对 DEX 文件或容器条目执行混淆对抗：用户规则与形近/不可见名启发式改类名与成员名
    /// （摘要返回 ProGuard 风格 mapping），可选的加密字符串还原则改写 const-string 操作数。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="outputPath">The output file path; for container entries it must differ from the input path.
    /// <para>输出文件路径；容器条目时必须与输入路径不同。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para></param>
    /// <param name="rules">User rename rules as <c>pattern=template</c> lines; empty uses heuristics only.
    /// <para>用户改名规则，格式为 <c>pattern=template</c> 行；为空时仅用启发式。</para></param>
    /// <param name="decryptStrings">Whether to detect and rewrite encrypted strings.
    /// <para>是否检测并改写加密字符串。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary carries counters and the mapping text.
    /// <para>操作结果，摘要含计数与 mapping 文本。</para></returns>
    public async Task<UotanStudio.Firmware.Core.FirmwareOpResult> DeobfuscateDexAsync(
        string path, string outputPath, string? entryName, IReadOnlyList<string> rules, bool decryptStrings, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var original = await ReadDexBytesAsync(path, entryName, ct).ConfigureAwait(false);
            DexRenameReport renameReport = new();
            DexStringDecryptReport? decryptReport = null;
            byte[] rebuilt = [];
            var summary = await Task.Run(() =>
            {
                var dex = DexFile.Load(original);
                var renameOptions = new DexRenameOptions
                {
                    Rules = rules.Select(ParseRule).ToList()
                };
                renameReport = DexRenameEngine.Rename(dex, renameOptions);
                if (decryptStrings)
                {
                    decryptReport = DexStringDecryptAnalyzer.Rewrite(dex);
                }
                rebuilt = dex.ToArray();
                var lines = new List<string>
                {
                    $"renamed classes={renameReport.RenamedClasses}, methods={renameReport.RenamedMethods}, fields={renameReport.RenamedFields}"
                };
                if (decryptReport is not null)
                {
                    lines.Add($"encrypted strings detected={decryptReport!.Candidates.Count}, rewritten sites={decryptReport.RewrittenSites}");
                }
                if (renameReport.Mapping.Length > 0)
                {
                    lines.Add("mapping:");
                    lines.Add(renameReport.Mapping);
                }
                return string.Join("\n", lines) + $"\nwritten to {outputPath} ({rebuilt.Length} bytes) 已写入 {outputPath}（{rebuilt.Length} 字节）";
            }, ct).ConfigureAwait(false);
            await WriteDexBytesAsync(path, outputPath, entryName, rebuilt, ct).ConfigureAwait(false);
            return UotanStudio.Firmware.Core.FirmwareOpResult.Ok(summary, [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Parses one <c>pattern=template</c> rename rule line.
    /// <para>解析一行 <c>pattern=template</c> 改名规则。</para>
    /// </summary>
    private static RenameRule ParseRule(string line)
    {
        var separator = line.IndexOf('=');
        return separator <= 0
            ? throw new ArgumentException($"Rename rule must be pattern=template: {line}")
            : new RenameRule(line[..separator], line[(separator + 1)..]);
    }

    /// <summary>
    /// Reads the DEX bytes of a plain file or the selected container entry.
    /// <para>读取普通文件或所选容器条目的 DEX 字节。</para>
    /// </summary>
    private static async Task<byte[]> ReadDexBytesAsync(string path, string? entryName, CancellationToken ct)
    {
        if (!JarDexContainer.IsContainerPath(path))
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        var resolved = ResolveEntryName(path, entryName);
        return await Task.Run(() => JarDexContainer.LoadEntry(path, resolved, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes rebuilt DEX bytes to the output path or back into the container entry.
    /// <para>把重建的 DEX 字节写到输出路径或写回容器条目。</para>
    /// </summary>
    private static async Task WriteDexBytesAsync(string path, string outputPath, string? entryName, byte[] bytes, CancellationToken ct)
    {
        if (!JarDexContainer.IsContainerPath(path))
        {
            await File.WriteAllBytesAsync(outputPath, bytes, ct).ConfigureAwait(false);
            return;
        }
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(outputPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            var resolved = ResolveEntryName(path, entryName);
            await Task.Run(() => JarDexContainer.WriteBack(path, resolved, bytes, ct), ct).ConfigureAwait(false);
            return;
        }
        await File.WriteAllBytesAsync(outputPath, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs a JAR/APK container or a package without DEX using a v1, v2 or v3 scheme combination into a new file.
    /// <para>以 v1、v2 或 v3 方案组合对 JAR/APK 容器或无 DEX 的包签名，写出到新文件。</para>
    /// </summary>
    /// <param name="inputPath">The input container path. <para>输入容器路径。</para></param>
    /// <param name="outputPath">The output container path; must differ from the input. <para>输出容器路径，必须与输入不同。</para></param>
    /// <param name="keyPath">The PEM, DER or PKCS#8 private key path. <para>PEM、DER 或 PKCS#8 私钥路径。</para></param>
    /// <param name="certPath">The PEM, DER or PFX certificate path. <para>PEM、DER 或 PFX 证书路径。</para></param>
    /// <param name="options">Signing options; <see langword="null"/> uses the defaults. <para>签名选项；空值使用默认设置。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary names the signed output path. <para>操作结果，摘要指明签名输出路径。</para></returns>
    public Task<UotanStudio.Firmware.Core.FirmwareOpResult> SignJarAsync(
        string inputPath, string outputPath, string keyPath, string certPath, ApkSigningOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ApkSigner.Sign(inputPath, outputPath, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null), options, ct);
            return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Ok($"Signed JAR written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.Core.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Loads a DEX from a plain file or, for a container path, from the bounded entry it selects.
    /// <para>从普通文件加载 DEX；容器路径则按选定条目有界读取。</para>
    /// </summary>
    /// <param name="path">The DEX or container file path. <para>DEX 或容器文件路径。</para></param>
    /// <param name="entryName">The container DEX entry name; empty picks the first DEX entry. <para>容器内 DEX 条目名；为空时取第一个 DEX 条目。</para></param>
    /// <returns>The loaded DEX file. <para>已加载的 DEX 文件。</para></returns>
    private static DexFile LoadDex(string path, string? entryName)
    {
        if (!JarDexContainer.IsContainerPath(path))
        {
            return DexFile.Load(path);
        }

        var resolved = ResolveEntryName(path, entryName);
        return DexFile.Load(JarDexContainer.LoadEntry(path, resolved));
    }

    /// <summary>
    /// Resolves a container DEX entry name, validating explicit names and picking the first entry when empty.
    /// <para>解析容器内 DEX 条目名，校验显式名称并在为空时取第一个条目。</para>
    /// </summary>
    /// <param name="path">The container file path. <para>容器文件路径。</para></param>
    /// <param name="entryName">The requested entry name; empty picks the first DEX entry. <para>请求的条目名；为空时取第一个 DEX 条目。</para></param>
    /// <returns>The validated entry name. <para>经校验的条目名。</para></returns>
    private static string ResolveEntryName(string path, string? entryName)
    {
        var entries = JarDexContainer.ListDexEntries(path);
        if (!string.IsNullOrWhiteSpace(entryName))
        {
            return entries.Contains(entryName, StringComparer.Ordinal)
                ? entryName
                : throw new InvalidDataException($"DEX entry {entryName} does not exist in the container.");
        }

        return entries.FirstOrDefault()
            ?? throw new InvalidDataException("The container has no classes*.dex entry.");
    }
}