using System.Text;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Decompile;
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