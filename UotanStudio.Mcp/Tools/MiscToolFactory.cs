using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Decompile;
using UotanStudio.Firmware.AndroidMeta.Dex.Obfuscation;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using UotanStudio.Hash.Digests;
using UotanStudio.Net.Http;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the miscellaneous format MCP tools (DEX/APK/AXML/ARSC) backed by the AndroidMeta
/// bounded context's public APIs.
/// <para>基于 AndroidMeta 限定上下文的公共 API 构建杂项格式（DEX/APK/AXML/ARSC）MCP 工具。</para>
/// Every delegate ends with the ambient <see cref="CancellationToken"/> the MCP server binds
/// itself; it does not appear in the emitted <c>inputSchema</c>. Only the backing APIs that accept
/// a token observe cancellation while working; the synchronous parsers and assemblers without a
/// token parameter stop before the work starts at most.
/// <para>每个委托末尾都是 MCP 服务器自行绑定的环境参数 <see cref="CancellationToken"/>；它不出现在
/// 生成的 <c>inputSchema</c> 中。只有接受令牌的底层 API 会在执行期间响应取消；没有令牌参数的
/// 同步解析器与汇编器至多在工作开始前中止。</para>
/// </summary>
internal static class MiscToolFactory
{
    internal static McpServerTool CreateDexDisassemble(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("DEX file path inside the sandbox.")] string path,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexDisassembleAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_disassemble",
                Description = "Disassemble a .dex file into smali text.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexDecompile(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("DEX file path inside the sandbox.")] string path,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexDecompileAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_decompile",
                Description = "Decompile a .dex file into readable Java-like source text "
                    + "using the built-in lightweight decompiler.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexAssemble(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Smali text to assemble.")] string smali,
             [Description("Output .dex file path inside the sandbox.")] string outputPath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexAssembleAsync(sandbox, smali, outputPath, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_assemble",
                Description = "Assemble smali text into a .dex file.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexInfo(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("DEX file path inside the sandbox.")] string path,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexInfoAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_info",
                Description = "Summarize a .dex file: version, string/type/field/method counts "
                    + "and class list.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateAxmlParse(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Compiled binary XML (AXML) file path inside the sandbox.")] string path,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.AxmlParseAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "axml_parse",
                Description = "Parse a compiled Android binary XML file (layouts, manifest) "
                    + "into text.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateArscParse(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("resources.arsc file path inside the sandbox.")] string path,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.ArscParseAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "arsc_parse",
                Description = "Parse a resources.arsc resource table into a structural summary.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateApkStripSignatures(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Input APK path inside the sandbox.")] string inputPath,
             [Description("Output APK path inside the sandbox (must differ from the input).")] string outputPath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.ApkStripSignaturesAsync(sandbox, inputPath, outputPath, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "apk_strip_signatures",
                Description = "Rewrite an APK dropping META-INF v1 signature entries "
                    + "(MANIFEST.MF/.SF/.RSA/.DSA/.EC) and v2/v3 signing blocks at the ZIP layer.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexRepartition(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Input container (JAR/APK) or plain .dex file path inside the sandbox.")] string path,
             [Description("Output container path inside the sandbox (must differ from the input); for a plain .dex the parts are written as <output>_partN.dex.")] string outputPath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexRepartitionAsync(sandbox, path, outputPath, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_repartition",
                Description = "Rebalance classes across classes*.dex entries (or split one .dex) so every "
                    + "part stays inside the 16-bit identifier-table limits. Strips v1 signature entries "
                    + "and invalidates the APK Signing Block; re-sign before installing.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateApkVerifySignatures(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("APK file path inside the sandbox.")] string path,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.ApkVerifySignaturesAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "apk_verify_signatures",
                Description = "Verify the v1/v2/v3 APK signature schemes for real: recompute entry and "
                    + "content digests, check the CMS and block signatures against the signer "
                    + "certificates, and report one status per scheme.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexStripDebug(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Input .dex file or container (JAR/APK) path inside the sandbox.")] string path,
             [Description("Output .dex file path inside the sandbox; when the input is a container and the output differs from it, the result lands there as a plain .dex, otherwise the selected entry is rewritten in place.")] string outputPath,
             [Description("Also clear the per-class .source file name.")] bool clearSourceFile = true,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexStripDebugAsync(sandbox, path, outputPath, clearSourceFile, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_strip_debug",
                Description = "Remove debug information (.line/.local/.param/.prologue/.source) from a dex "
                    + "and rebuild it, shrinking the image; strings referenced only by debug content leave "
                    + "the string pool.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexRepair(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Input .dex file or container (JAR/APK) path inside the sandbox.")] string path,
             [Description("Output .dex file path inside the sandbox; when the input is a container and the output differs from it, the result lands there as a plain .dex, otherwise the selected entry is rewritten in place.")] string outputPath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexRepairAsync(sandbox, path, outputPath, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_repair",
                Description = "Repair a damaged dex via lenient re-parse and full rebuild: recomputes the "
                    + "SHA-1 signature and Adler-32 checksum and cuts trailing bytes beyond the declared "
                    + "file size, reporting every action taken.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDexDeobfuscate(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Input .dex file or container (JAR/APK) path inside the sandbox.")] string path,
             [Description("Output .dex file path inside the sandbox.")] string outputPath,
             [Description("Optional rename rules, one pattern=template line each (templates support {name}/{class}/{counter}/{z2}..{z5}).")] string[]? renameRules = null,
             [Description("Also detect and rewrite encrypted strings (XOR with 1-8 byte keys, Base64).")] bool decryptStrings = false,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DexDeobfuscateAsync(sandbox, path, outputPath, renameRules, decryptStrings, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "dex_deobfuscate",
                Description = "Counter name obfuscation by renaming look-alike/invisible member names and "
                    + "classes via user rules (with a ProGuard-style mapping), optionally rewriting "
                    + "encrypted strings. String literals are never touched.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateDownloadFile(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Direct http(s) download URL.")] string url,
             [Description("Output file path inside the sandbox.")] string outputPath,
             [Description("Optional SHA-256 hex digest; the download fails when it does not match.")] string? sha256 = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => MiscToolHandlers.DownloadFileAsync(sandbox, url, outputPath, sha256, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "download_file",
                Description = "Download a file over http(s) into the sandbox with streaming and atomic "
                    + "publish: bytes land in a temporary file that replaces the output only after the "
                    + "hash check passes, so cancelling or a digest mismatch leaves the previous file "
                    + "untouched and never a truncated download.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });
}

/// <summary>
/// Implementations for the misc tool delegates; kept separate for direct unit testing.
/// <para>杂项工具委托的实现；独立存放便于直接单元测试。</para>
/// All handlers return the <see cref="ToolResult"/> envelope and <see cref="ToolGuard"/> turns it
/// into the protocol result. Parsing and assembly APIs are synchronous CPU work; heavy calls run
/// on the thread pool, where the request's token can stop the work before it starts but not
/// interrupt a parser that takes no token. <see cref="ApkEntryStripper.Rewrite"/> is the one
/// backing API that observes cancellation while copying entries.
/// <para>所有处理器返回 <see cref="ToolResult"/> 信封，由 <see cref="ToolGuard"/> 转为协议结果。
/// 解析与汇编 API 是同步 CPU 操作，重活推到线程池执行：令牌可在工作开始前阻止执行，但无法打断
/// 不接收令牌的解析器。<see cref="ApkEntryStripper.Rewrite"/> 是唯一在复制条目期间响应取消的
/// 底层 API。</para>
/// </summary>
internal static class MiscToolHandlers
{
    private const long MaximumMetaFileBytes = 512L * 1024 * 1024;

    public static Task<ToolResult> DexDisassembleAsync(PathSandbox sandbox, string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var dex = DexFile.Load(resolved);
            var text = Disassembler.DisassembleAll(dex);
            return new ToolResult(true,
                $"Disassembled {Path.GetFileName(resolved)} ({text.Length} chars).",
                ToolJson.Data(new PathTextPayload(resolved, text)));
        }, cancellationToken);

    public static Task<ToolResult> DexDecompileAsync(PathSandbox sandbox, string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var dex = DexFile.Load(resolved);
            var text = LightweightDecompiler.DecompileAll(dex);
            return new ToolResult(true,
                $"Decompiled {Path.GetFileName(resolved)} ({text.Length} chars).",
                ToolJson.Data(new PathTextPayload(resolved, text)));
        }, cancellationToken);

    public static Task<ToolResult> DexAssembleAsync(PathSandbox sandbox, string smali, string outputPath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(smali);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);
            var dex = SmaliAssembler.Assemble(smali);
            dex.Write(resolvedOutput);
            return new ToolResult(true,
                $"Assembled dex written: {resolvedOutput}",
                ToolJson.Data(new OutputBytesPayload(resolvedOutput, new FileInfo(resolvedOutput).Length)));
        }, cancellationToken);

    public static Task<ToolResult> DexInfoAsync(PathSandbox sandbox, string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var dex = DexFile.Load(resolved);
            var classes = dex.Classes.Select(c => dex.GetType(c.TypeIndex)).ToList();
            return new ToolResult(true, dex.Describe(), ToolJson.Data(new DexInfoPayload(
                resolved,
                dex.Strings.Count,
                dex.Types.Count,
                dex.Fields.Count,
                dex.Methods.Count,
                classes)));
        }, cancellationToken);

    public static Task<ToolResult> AxmlParseAsync(PathSandbox sandbox, string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var data = File.ReadAllBytes(resolved);
            if (!AXmlParser.IsAxml(data))
            {
                return new ToolResult(false,
                    $"Not a compiled binary XML file: {resolved}");
            }
            var document = AXmlParser.Parse(data);
            var text = document.ToXml();
            return new ToolResult(true,
                $"Parsed AXML {Path.GetFileName(resolved)}.", ToolJson.Data(new PathXmlPayload(resolved, text)));
        }, cancellationToken);

    public static Task<ToolResult> ArscParseAsync(PathSandbox sandbox, string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var table = Firmware.AndroidMeta.Arsc.ArscParser.Parse(File.ReadAllBytes(resolved));
            var packages = table.Packages.Select(p => new ArscPackagePayload(
                p.Name,
                (int)p.Id,
                p.Types.Count)).ToList();
            return new ToolResult(true,
                $"Parsed resources.arsc with {packages.Count} package(s).",
                ToolJson.Data(new ArscSummaryPayload(resolved, packages)));
        }, cancellationToken);

    public static Task<ToolResult> ApkStripSignaturesAsync(PathSandbox sandbox, string inputPath, string outputPath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);
            if (string.Equals(Path.GetFullPath(resolvedInput), Path.GetFullPath(resolvedOutput),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return new ToolResult(false,
                    "Output APK path must differ from the input path.");
            }
            ApkEntryStripper.Rewrite(resolvedInput, resolvedOutput,
                new ApkEntryStripper.RewriteRequest { StripV1SignatureEntries = true }, cancellationToken);
            return new ToolResult(true,
                $"Signature entries stripped into: {resolvedOutput}",
                ToolJson.Data(new InputOutputPayload(resolvedInput, resolvedOutput)));
        }, cancellationToken);

    public static Task<ToolResult> ApkVerifySignaturesAsync(PathSandbox sandbox, string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var verification = ApkSignatureVerifier.Verify(resolved, cancellationToken);
            var status = new Dictionary<string, string>
            {
                ["v1"] = verification.V1.ToString(),
                ["v2"] = verification.V2.ToString(),
                ["v3"] = verification.V3.ToString()
            };
            var summary = $"v1={status["v1"]}, v2={status["v2"]}, v3={status["v3"]}"
                + (verification.V3MinSdk is { } minSdk ? $"; v3 sdk range {minSdk}-{verification.V3MaxSdk}" : string.Empty)
                + (verification.V3HasProtectionAttribute ? "; v3 anti-stripping attribute present" : string.Empty)
                + (verification.SignerSubjects.Count > 0
                    ? "; signers: " + string.Join(" | ", verification.SignerSubjects.Distinct())
                    : string.Empty)
                + (verification.Problems.Count > 0
                    ? "; problems: " + string.Join("; ", verification.Problems.Take(10))
                    : string.Empty);
            return new ToolResult(true, summary, ToolJson.Data(new
            {
                file = resolved,
                schemes = status,
                verification.V3MinSdk,
                verification.V3MaxSdk,
                verification.V3HasProtectionAttribute,
                verification.SignerSubjects,
                verification.Problems
            }));
        }, cancellationToken);

    public static async Task<ToolResult> DexDeobfuscateAsync(PathSandbox sandbox, string path, string outputPath, string[]? renameRules, bool decryptStrings, CancellationToken cancellationToken)
    {
        var resolved = sandbox.Resolve(path, mustExist: true);
        GuardMetaSize(resolved);
        var resolvedOutput = sandbox.ResolveOutput(outputPath);
        if (string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(resolvedOutput),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new ToolResult(false, "Output path must differ from the input path.");
        }
        return await Task.Run(async () =>
        {
            var dex = JarDexContainer.IsContainerPath(resolved)
                ? DexFile.Load(JarDexContainer.LoadEntry(resolved, JarDexContainer.ListDexEntries(resolved)[0], cancellationToken))
                : DexFile.Load(resolved);
            var renameOptions = new DexRenameOptions
            {
                Rules = (renameRules ?? []).Select(ParseRenameRule).ToList()
            };
            var renameReport = DexRenameEngine.Rename(dex, renameOptions);
            var decryptReport = decryptStrings ? DexStringDecryptAnalyzer.Rewrite(dex) : null;
            var rebuilt = dex.ToArray();
            await File.WriteAllBytesAsync(resolvedOutput, rebuilt, cancellationToken).ConfigureAwait(false);
            var summary = $"renamed classes={renameReport.RenamedClasses}, methods={renameReport.RenamedMethods}, fields={renameReport.RenamedFields}"
                + (decryptReport is null
                    ? string.Empty
                    : $"; encrypted strings detected={decryptReport.Candidates.Count}, rewritten sites={decryptReport.RewrittenSites}")
                + $"; {rebuilt.Length} bytes written. Mapping:\n{renameReport.Mapping}";
            return new ToolResult(true, summary, ToolJson.Data(new OutputBytesPayload(resolvedOutput, rebuilt.Length)));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RenameRule ParseRenameRule(string line)
    {
        var separator = line.IndexOf('=');
        return separator <= 0
            ? throw new ArgumentException($"Rename rule must be pattern=template: {line}")
            : new RenameRule(line[..separator], line[(separator + 1)..]);
    }

    public static async Task<ToolResult> DexStripDebugAsync(PathSandbox sandbox, string path, string outputPath, bool clearSourceFile, CancellationToken cancellationToken)
        => await Task.Run(async () =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);
            byte[] rebuilt;
            if (JarDexContainer.IsContainerPath(resolved)
                && string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(resolvedOutput),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                var entryNames = JarDexContainer.ListDexEntries(resolved);
                if (entryNames.Count == 0)
                {
                    return new ToolResult(false, $"The container holds no classes*.dex entry: {resolved}");
                }
                var entryBytes = JarDexContainer.LoadEntry(resolved, entryNames[0], cancellationToken);
                rebuilt = DexPostProcessor.StripDebugInfoRebuilt(entryBytes, options: null, clearSourceFile);
                JarDexContainer.WriteBack(resolved, entryNames[0], rebuilt, cancellationToken);
                return new ToolResult(true,
                    $"Debug info stripped from {entryNames[0]}: {entryBytes.Length} → {rebuilt.Length} bytes, written back in place.",
                    ToolJson.Data(new OutputBytesPayload(resolvedOutput, rebuilt.Length)));
            }

            var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
            rebuilt = DexPostProcessor.StripDebugInfoRebuilt(bytes, options: null, clearSourceFile);
            await File.WriteAllBytesAsync(resolvedOutput, rebuilt, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true,
                $"Debug info stripped: {bytes.Length} → {rebuilt.Length} bytes, written to {resolvedOutput}",
                ToolJson.Data(new OutputBytesPayload(resolvedOutput, rebuilt.Length)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> DexRepairAsync(PathSandbox sandbox, string path, string outputPath, CancellationToken cancellationToken)
        => await Task.Run(async () =>
        {
            var resolved = sandbox.Resolve(path, mustExist: true);
            GuardMetaSize(resolved);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);
            byte[] repaired;
            DexRepairReport report;
            if (JarDexContainer.IsContainerPath(resolved)
                && string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(resolvedOutput),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                var entryNames = JarDexContainer.ListDexEntries(resolved);
                if (entryNames.Count == 0)
                {
                    return new ToolResult(false, $"The container holds no classes*.dex entry: {resolved}");
                }
                var entryBytes = JarDexContainer.LoadEntry(resolved, entryNames[0], cancellationToken);
                repaired = DexPostProcessor.Repair(entryBytes, out report);
                JarDexContainer.WriteBack(resolved, entryNames[0], repaired, cancellationToken);
                return new ToolResult(true, DescribeRepair(entryNames[0], repaired.Length, report),
                    ToolJson.Data(new OutputBytesPayload(resolvedOutput, repaired.Length)));
            }

            var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
            repaired = DexPostProcessor.Repair(bytes, out report);
            await File.WriteAllBytesAsync(resolvedOutput, repaired, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true, DescribeRepair(resolvedOutput, repaired.Length, report),
                ToolJson.Data(new OutputBytesPayload(resolvedOutput, repaired.Length)));
        }, cancellationToken).ConfigureAwait(false);

    private static string DescribeRepair(string target, int length, DexRepairReport report)
    {
        var actions = new List<string>();
        if (report.ChecksumMismatched) actions.Add("Adler-32 checksum recomputed");
        if (report.SignatureMismatched) actions.Add("SHA-1 signature recomputed");
        if (report.TruncatedTrailingBytes) actions.Add($"{report.TruncatedByteCount} trailing bytes cut");
        if (actions.Count == 0) actions.Add("tables, signature and checksum rebuilt");
        return $"Repaired {target} ({length} bytes): {string.Join("; ", actions)}.";
    }

    public static async Task<ToolResult> DexRepartitionAsync(PathSandbox sandbox, string path, string outputPath, CancellationToken cancellationToken)
    {
        var resolvedInput = sandbox.Resolve(path, mustExist: true);
        GuardMetaSize(resolvedInput);
        if (string.Equals(Path.GetFullPath(resolvedInput), Path.GetFullPath(sandbox.ResolveOutput(outputPath)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new ToolResult(false, "Output path must differ from the input path.");
        }
        return await Task.Run(async () =>
        {
            IReadOnlyList<byte[]> parts;
            var partFiles = new List<string>();
            if (JarDexContainer.IsContainerPath(resolvedInput))
            {
                var resolvedOutput = sandbox.ResolveOutput(outputPath);
                var entryNames = JarDexContainer.ListDexEntries(resolvedInput);
                if (entryNames.Count == 0)
                {
                    return new ToolResult(false, $"The container holds no classes*.dex entry: {resolvedInput}");
                }
                var sourceBytes = entryNames
                    .Select(name => JarDexContainer.LoadEntry(resolvedInput, name, cancellationToken))
                    .ToArray();
                parts = DexRepartitioner.Rebalance(sourceBytes, cancellationToken);
                var entries = new List<(string Name, byte[] Content)>();
                for (var i = 0; i < parts.Count; i++)
                {
                    entries.Add((i == 0 ? "classes.dex" : $"classes{i + 1}.dex", parts[i]));
                }
                File.Copy(resolvedInput, resolvedOutput, overwrite: false);
                JarDexContainer.RewriteDexEntries(resolvedOutput, entries, stripSignatureEntries: true, cancellationToken);
                partFiles.Add(resolvedOutput);
            }
            else
            {
                var stem = Path.GetFileNameWithoutExtension(resolvedInput);
                var inputBytes = await File.ReadAllBytesAsync(resolvedInput, cancellationToken).ConfigureAwait(false);
                parts = DexRepartitioner.Split(inputBytes, cancellationToken);
                var outputBase = sandbox.ResolveOutput(outputPath);
                var directory = Path.GetDirectoryName(outputBase)!;
                Directory.CreateDirectory(directory);
                for (var i = 0; i < parts.Count; i++)
                {
                    var partPath = Path.Combine(directory, $"{stem}_part{i + 1}.dex");
                    await File.WriteAllBytesAsync(partPath, parts[i], cancellationToken).ConfigureAwait(false);
                    partFiles.Add(partPath);
                }
            }
            return new ToolResult(true,
                $"Repartitioned into {parts.Count} dex part(s); v1 signature entries and the APK Signing "
                + "Block were dropped, re-sign before installing.",
                ToolJson.Data(new InputOutputPayload(resolvedInput, string.Join(";", partFiles))));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a file over http(s) into the sandbox through the shared download entry point, verifies the
    /// optional SHA-256 digest against the staged file, and publishes the output with a single atomic move.
    /// <para>经统一下载入口把文件以 http(s) 下载进沙箱，对暂存文件校验可选的 SHA-256 摘要后以单次原子移动发布输出。</para>
    /// </summary>
    /// <param name="sandbox">Path sandbox that resolves the output path.<para>解析输出路径的路径沙箱。</para></param>
    /// <param name="url">Absolute http(s) URL to download.<para>要下载的绝对 http(s) 地址。</para></param>
    /// <param name="outputPath">Output file path inside the sandbox.<para>沙箱内的输出文件路径。</para></param>
    /// <param name="sha256">Optional expected SHA-256 hex digest; a mismatch fails without touching the output.
    /// <para>可选的期望 SHA-256 十六进制摘要；不匹配时失败且不改动输出。</para></param>
    /// <param name="cancellationToken">Client cancellation token; the download additionally runs on a five-minute budget.
    /// <para>客户端取消令牌；下载另有五分钟预算。</para></param>
    /// <returns>A success result reporting the published file, or a failure result for an invalid URL, a digest
    /// mismatch, or the five-minute timeout.
    /// <para>成功结果报告已发布文件；地址非法、摘要不匹配或五分钟超时返回失败结果。</para></returns>
    public static async Task<ToolResult> DownloadFileAsync(PathSandbox sandbox, string url,
        string outputPath, string? sha256, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var resolved = sandbox.ResolveOutput(outputPath);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new ToolResult(false, "Only absolute http(s) URLs are supported.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        var staging = resolved + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            using var client = new HttpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await HttpDownload.DownloadAsync(client, uri, staging, cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            string? actual = null;
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                byte[] expected;
                try
                {
                    expected = Convert.FromHexString(sha256);
                }
                catch (FormatException)
                {
                    return new ToolResult(false, "The supplied sha256 is not valid hexadecimal.");
                }

                if (expected.Length != 32)
                {
                    return new ToolResult(false, "sha256 must be 64 hexadecimal characters.");
                }

                await using var stream = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualHash = await Sha256.ComputeHashAsync(stream, timeout.Token)
                    .ConfigureAwait(false);
                actual = Convert.ToHexStringLower(actualHash);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, expected))
                {
                    return new ToolResult(false,
                        $"SHA-256 mismatch: expected {sha256}, downloaded {actual}. The output was not modified.");
                }
            }

            File.Move(staging, resolved, overwrite: true);
            return new ToolResult(true,
                $"Downloaded {new FileInfo(resolved).Length} bytes into {resolved}"
                    + (actual is null ? string.Empty : $" (sha256 {actual})"),
                ToolJson.Data(new DownloadFilePayload(url, resolved, new FileInfo(resolved).Length,
                    actual, actual is not null)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolResult(false,
                "The download timed out after 5 minutes. The output was not modified.");
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    /// <summary>
    /// Keeps metadata parsing bounded even though the parsers stream internally.
    /// <para>尽管解析器内部流式处理，仍在入口限制元数据文件大小。</para>
    /// </summary>
    private static void GuardMetaSize(string resolved)
    {
        var info = new FileInfo(resolved);
        if (info.Length > MaximumMetaFileBytes)
        {
            throw new InvalidDataException(
                $"File exceeds the {MaximumMetaFileBytes / 1024 / 1024} MiB metadata tool limit: {resolved}");
        }
    }
}