using System.Text;
using UotanStudio.Firmware.Dex;
using UotanStudio.Firmware.Dex.Decompile;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>DEX 综合服务，封装 DEX 文件解析、反汇编（Smali 文本）与重建写入功能，基于 UotanStudio.Firmware.Dex 生态。</para>
/// Dex integrated service, wrapping DEX file parsing, disassembly (Smali text) and rebuild-write capabilities,
/// built on the UotanStudio.Firmware.Dex ecosystem.
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
    /// <para>解析 DEX 文件并返回其描述信息（类、方法、字段等统计）。</para>
    /// Parses a DEX file and returns its description (class, method, field statistics, etc.).
    /// </summary>
    /// <param name="path">DEX 文件路径。<para>DEX file path.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；成功时 Summary 为文件描述，失败时 Summary 为异常信息。</returns>
    public Task<UotanStudio.Firmware.Common.FirmwareOpResult> ParseDexAsync(string path, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = DexFile.Load(path);
            var summary = dex.Describe();
            return Task.FromResult(UotanStudio.Firmware.Common.FirmwareOpResult.Ok(summary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.Common.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// <para>将 DEX 文件反汇编为 Smali 文本，写入指定目录（默认取 DEX 同目录的 <c>&lt;name&gt;_dexout</c> 文件夹）下的
    /// <c>&lt;name&gt;.smali</c> 文件。</para>
    /// Disassembles a DEX file into Smali text, written to the specified directory (defaults to the
    /// <c>&lt;name&gt;_dexout</c> folder next to the DEX file) as a <c>&lt;name&gt;.smali</c> file.
    /// </summary>
    /// <param name="path">DEX 文件路径。<para>DEX file path.</para></param>
    /// <param name="outputDirectory">输出目录；为空时自动取 DEX 同目录下的 <c>&lt;name&gt;_dexout</c> 文件夹。</param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出文件路径。</returns>
    public async Task<UotanStudio.Firmware.Common.FirmwareOpResult> DisassembleDexAsync(string path, string? outputDirectory, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = DexFile.Load(path);
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
            return UotanStudio.Firmware.Common.FirmwareOpResult.Ok(summary, [smaliPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Common.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// <para>加载 DEX 文件并重建写入到指定输出路径。</para>
    /// Loads a DEX file and rebuilds it to the specified output path.
    /// </summary>
    /// <param name="path">DEX 文件路径。<para>DEX file path.</para></param>
    /// <param name="outputPath">输出文件路径。<para>Output file path.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出路径。</returns>
    public Task<UotanStudio.Firmware.Common.FirmwareOpResult> RebuildDexAsync(string path, string outputPath, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = DexFile.Load(path);
            dex.Write(outputPath);
            return Task.FromResult(UotanStudio.Firmware.Common.FirmwareOpResult.Ok($"Rebuilt DEX written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(UotanStudio.Firmware.Common.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// <para>反编译 DEX 文件为伪 Java 源码，写入指定路径（默认取 DEX 同目录的 <c>&lt;name&gt;.java</c>）。
    /// 内置重载解析；输出模式由 <see cref="DecompileOptions"/> 控制。</para>
    /// Decompiles a DEX file into pseudo-Java source, written to the specified path (defaults to
    /// <c>&lt;name&gt;.java</c> next to the DEX file). Overload resolution is built in; the output
    /// mode is controlled by <see cref="DecompileOptions"/>.
    /// </summary>
    /// <param name="path">DEX 文件路径。<para>DEX file path.</para></param>
    /// <param name="outputPath">输出文件路径；为空时自动取 DEX 同目录下的 <c>&lt;name&gt;.java</c>。</param>
    /// <param name="options">反编译选项；<c>null</c> 用默认值。<para>Decompiler options; <c>null</c> for defaults.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>操作结果；Summary 含输出文件路径与字节数。</returns>
    public async Task<UotanStudio.Firmware.Common.FirmwareOpResult> DecompileDexAsync(string path, string? outputPath, DecompileOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var dex = DexFile.Load(path);
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
            return UotanStudio.Firmware.Common.FirmwareOpResult.Ok(summary, [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UotanStudio.Firmware.Common.FirmwareOpResult.Fail(ex.Message);
        }
    }
}