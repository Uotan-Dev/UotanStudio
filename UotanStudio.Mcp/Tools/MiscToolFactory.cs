using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Decompile;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;

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
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var dexDisassemble = McpServerTool.Create(
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

        var dexDecompile = McpServerTool.Create(
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

        var dexAssemble = McpServerTool.Create(
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

        var dexInfo = McpServerTool.Create(
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

        var axmlParse = McpServerTool.Create(
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

        var arscParse = McpServerTool.Create(
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

        var apkStrip = McpServerTool.Create(
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

        return [dexDisassemble, dexDecompile, dexAssemble, dexInfo, axmlParse, arscParse, apkStrip];
    }
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
            var table = UotanStudio.Firmware.AndroidMeta.Arsc.ArscParser.Parse(File.ReadAllBytes(resolved));
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