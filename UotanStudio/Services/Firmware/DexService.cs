using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Containers;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>DEX 综合服务，封装 DEX 文件解析、反汇编（Smali 文本）与重建写入功能，基于 UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex 生态。
/// 支持直接读取 DEX 文件或 JAR/APK 容器内的 classes*.dex 条目。</para>
/// Dex integrated service, wrapping DEX file parsing, disassembly (Smali text) and rebuild-write capabilities,
/// built on the UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex ecosystem. Supports plain DEX files as
/// well as classes*.dex entries inside JAR/APK containers.
/// </summary>
public sealed class DexService
{
    private static readonly Lazy<DexService> LazyInstance = new(() => new DexService());

    /// <summary>
    /// <para>获取 DexService 单例。</para>
    /// Gets the DexService singleton instance.
    /// </summary>
    public static DexService Instance => LazyInstance.Value;

    private DexService() { }

    /// <summary>
    /// <para>解析 DEX 文件并返回其描述信息（类、方法、字段等统计）；路径为 JAR/APK 容器时解析指定 DEX 条目。</para>
    /// Parses a DEX file and returns its description (class, method, field statistics, etc.);
    /// for a JAR/APK container path, parses the given DEX entry.
    /// </summary>
    /// <param name="path">DEX 或容器文件路径。<para>DEX or container file path.</para></param>
    /// <param name="entryName">
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para>
    /// The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// </param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；成功时 Summary 为文件描述，失败时 Summary 为异常信息。</returns>
    public Task<UotanStudio.Firmware.SharedKernel.FirmwareOpResult> ParseDexAsync(string path, string? entryName = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = LoadDex(path, entryName);
            var summary = dex.Describe();
            return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Ok(summary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// <para>将 DEX 文件反汇编为 Smali 文本，写入指定目录（默认取 DEX 同目录的 <c>&lt;name&gt;_dexout</c> 文件夹）下的
    /// <c>&lt;name&gt;.smali</c> 文件。</para>
    /// Disassembles a DEX file into Smali text, written to the specified directory (defaults to the
    /// <c>&lt;name&gt;_dexout</c> folder next to the DEX file) as a <c>&lt;name&gt;.smali</c> file.
    /// </summary>
    /// <param name="path">DEX 或容器文件路径。<para>DEX or container file path.</para></param>
    /// <param name="outputDirectory">输出目录；为空时自动取 DEX 同目录下的 <c>&lt;name&gt;_dexout</c> 文件夹。</param>
    /// <param name="entryName">容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。<para>Container DEX entry name; empty picks the first DEX entry, ignored for plain files.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出文件路径。</returns>
    public async Task<UotanStudio.Firmware.SharedKernel.FirmwareOpResult> DisassembleDexAsync(string path, string? outputDirectory, string? entryName = null, CancellationToken ct = default)
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
            return UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Ok(summary, [smaliPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// <para>加载 DEX（或容器内 DEX 条目）并重建写入到指定输出路径；来源为容器时经条目写回。</para>
    /// Loads a DEX (or a container DEX entry) and rebuilds it to the specified output path;
    /// container sources are written back through the entry.
    /// </summary>
    /// <param name="path">DEX 或容器文件路径。<para>DEX or container file path.</para></param>
    /// <param name="outputPath">输出文件路径。<para>Output file path.</para></param>
    /// <param name="entryName">
    /// <para>容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。</para>
    /// The container DEX entry name; empty picks the first DEX entry, ignored for plain files.
    /// </param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出路径。</returns>
    public Task<UotanStudio.Firmware.SharedKernel.FirmwareOpResult> RebuildDexAsync(string path, string outputPath, string? entryName = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (JarDexContainer.IsContainerPath(path))
            {
                var containerEntry = ResolveEntryName(path, entryName);
                JarDexContainer.WriteBack(path, containerEntry, LoadDex(path, containerEntry).ToArray(), ct);
                return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Ok(
                    $"Rebuilt {containerEntry} written back to {path}", [path]));
            }

            var dex = DexFile.Load(path);
            dex.Write(outputPath);
            return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Ok($"Rebuilt DEX written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// <para>反编译 DEX 文件为伪 Java 源码，写入指定路径（默认取 DEX 同目录的 <c>&lt;name&gt;.java</c>）。
    /// 内置重载解析；输出模式由 <see cref="DecompileOptions"/> 控制。</para>
    /// Decompiles a DEX file into pseudo-Java source, written to the specified path (defaults to
    /// <c>&lt;name&gt;.java</c> next to the DEX file). Overload resolution is built in; the output
    /// mode is controlled by <see cref="DecompileOptions"/>.
    /// </summary>
    /// <param name="path">DEX 或容器文件路径。<para>DEX or container file path.</para></param>
    /// <param name="outputPath">输出文件路径；为空时自动取 DEX 同目录下的 <c>&lt;name&gt;.java</c>。</param>
    /// <param name="entryName">容器内 DEX 条目名；为空时取第一个 DEX 条目，非容器路径忽略。<para>Container DEX entry name; empty picks the first DEX entry, ignored for plain files.</para></param>
    /// <param name="options">反编译选项；<c>null</c> 用默认值。<para>Decompiler options; <c>null</c> for defaults.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出文件路径与字节数。</returns>
    public async Task<UotanStudio.Firmware.SharedKernel.FirmwareOpResult> DecompileDexAsync(string path, string? outputPath, string? entryName = null, DecompileOptions? options = null, CancellationToken ct = default)
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
            return UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Ok(summary, [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// <para>对 JAR/APK 容器或 DEX 文件执行 v1/v2/v3 组合签名（复用 APK 签名栈），输出到新文件。
    /// 普通应用 JAR 通常只需 v1（JAR 规范工件），可按需启用 v2/v3。</para>
    /// Signs a JAR/APK container or DEX-less package with the v1/v2/v3 combination (reusing the APK
    /// signing stack) to a new output file. Plain application JARs usually need v1 only (JAR
    /// artifacts); v2/v3 can be enabled as needed.
    /// </summary>
    /// <param name="inputPath">输入容器路径。<para>The input container path.</para></param>
    /// <param name="outputPath">输出容器路径；必须与输入不同。<para>The output container path; must differ from the input.</para></param>
    /// <param name="keyPath">PEM/DER/PKCS#8 私钥路径。<para>The PEM/DER/PKCS#8 private key path.</para></param>
    /// <param name="certPath">PEM/DER/PFX 证书路径。<para>The PEM/DER/PFX certificate path.</para></param>
    /// <param name="options">签名选项；<c>null</c> 用默认值（v1+v2+v3、剥离旧签名）。<para>Signing options; <c>null</c> for defaults (v1+v2+v3, strip old signatures).</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出路径。</returns>
    public Task<UotanStudio.Firmware.SharedKernel.FirmwareOpResult> SignJarAsync(
        string inputPath, string outputPath, string keyPath, string certPath, ApkSigningOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ApkSigner.Sign(inputPath, outputPath, ApkSignerKeyMaterial.Load(keyPath, null, certPath, null), options, ct);
            return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Ok($"Signed JAR written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// <para>加载 DEX：容器路径经条目有界读取（为空时取第一个 DEX 条目），普通路径直接加载。</para>
    /// Loads a DEX: container paths read the bounded entry (the first DEX entry when empty);
    /// plain paths load directly.
    /// </summary>
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
    /// <para>解析容器内 DEX 条目名：显式指定时校验存在，为空时取第一个 DEX 条目，无 DEX 时报错。</para>
    /// Resolves a container DEX entry name: validates explicit names, picks the first DEX entry
    /// when empty, and errors when the container has no DEX entry.
    /// </summary>
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