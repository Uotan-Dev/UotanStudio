using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// <para>项目范围文本搜索的格式适配层：把项目内未打开的二进制格式（DTB、ARSC、AXML、
/// DEX/JAR）有界地文本化，复用现有 <see cref="ProjectTextSearchService"/> 的匹配与限额通道。
/// 适配只生成临时搜索文本，不修改原文件；魔数不识别、解析失败或超限时按跳过处理。</para>
/// The format-adapter layer for project-scope text search: bounded text-ification of unopened binary
/// formats inside a project (DTB, ARSC, AXML, DEX/JAR), reusing the existing
/// <see cref="ProjectTextSearchService"/> match and limit channel. Adaptation only produces
/// temporary search text and never mutates the source file; unrecognized magic, parse failures or
/// over-limit results are skipped.
/// </summary>
internal static class FormatSearchText
{
    /// <summary>
    /// <para>一次读取的文件头长度：取最长魔数判定的长度（ARSC 表头类型 + 头部大小 + 文件大小），
    /// AXML（8）与 DEX（8）判定复用同一头部。</para>
    /// Length of the single header read: the longest magic-check length (ARSC table type + header
    /// size + file size); AXML (8) and DEX (8) checks reuse the same header.
    /// </summary>
    private const int ArscHeaderLength = 12;

    /// <summary>
    /// <para>DEX 魔数判定所需的头部长度（"dex\\n" + 版本 + NUL）。</para>
    /// Header length needed for DEX magic detection ("dex\\n" + version + NUL).
    /// </summary>
    private const int DexHeaderLength = 8;

    /// <summary>
    /// <para>DTB 魔数判定所需的头部长度（大端 FDT 魔数）。</para>
    /// Header length needed for DTB magic detection (big-endian FDT magic).
    /// </summary>
    private const int DtbHeaderLength = 4;

    /// <summary>
    /// <para>按扩展名/魔数识别并文本化项目文件；无法识别或超限时返回 <c>null</c>（调用方跳过）。</para>
    /// Identifies and text-ifies a project file by extension/magic; returns <c>null</c> when
    /// unrecognized or over-limit (the caller skips it).
    /// </summary>
    /// <param name="path">项目文件路径。<para>The project file path.</para></param>
    /// <param name="cancellationToken">取消令牌。<para>The cancellation token.</para></param>
    /// <returns>可搜索文本；跳过时为 <c>null</c>。<para>The searchable text; <c>null</c> when skipped.</para></returns>
    internal static string? TryRead(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        // 导航路径在项目扫描之外调用本方法，这里再核对一次输入尺寸，避免把超大文件整读入内存。
        // The navigation path calls this outside the project scan, so the input size is re-checked here
        // to avoid reading an oversized file fully into memory.
        if (new FileInfo(path).Length > TextSearchService.MaximumTextLength)
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        // 只读一次头部（覆盖最长的 ARSC 判定长度），所有魔数判定复用，避免多次打开文件。
        // Read the header once (covering the longest ARSC detection length) and reuse it for every
        // magic check, avoiding repeated file opens.
        byte[] header;
        try
        {
            header = UotanStudio.Firmware.SharedKernel.FormatDetector.ReadHeader(path, ArscHeaderLength);
        }
        catch
        {
            header = [];
        }

        try
        {
            if (FileOpenMode.IsDtbExtension(extension) || IsDtbMagic(header))
            {
                return Bound(ReadDtb(path));
            }

            if (FileOpenMode.IsArscExtension(extension) || ArscParser.IsArsc(header))
            {
                return Bound(ReadArsc(path, cancellationToken));
            }

            if (FileOpenMode.IsDexExtension(extension) || IsDexMagic(header))
            {
                return Bound(ReadDex(path, cancellationToken));
            }

            if (FileOpenMode.IsJarExtension(extension))
            {
                return Bound(ReadContainer(path, cancellationToken));
            }

            if (IsManifestPath(path) || AXmlParser.IsAxml(header))
            {
                return Bound(ReadAxml(path, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 魔数不识别、解析失败或读取错误都按“跳过该文件”处理，与项目搜索的跳过语义一致。
            // Unrecognized magic, parse failures and read errors all skip the file, matching the
            // project search's skip semantics.
            return null;
        }

        return null;
    }

    /// <summary>
    /// <para>把 DTB 字节解析为 DTS 文本。</para>
    /// Parses DTB bytes into DTS text.
    /// </summary>
    private static string ReadDtb(string path) => DtsFormat.Write(DtbFormat.Read(File.ReadAllBytes(path)));

    /// <summary>
    /// <para>把 ARSC 字节解析为资源表搜索文本。</para>
    /// Parses ARSC bytes into resource-table search text.
    /// </summary>
    private static string ReadArsc(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var table = ArscParser.Parse(File.ReadAllBytes(path));
        cancellationToken.ThrowIfCancellationRequested();
        return ResourceTableSearchText.Serialize(table);
    }

    /// <summary>
    /// <para>把普通 DEX 字节反汇编为 Smali 文本。</para>
    /// Disassembles plain DEX bytes into Smali text.
    /// </summary>
    private static string ReadDex(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Disassembler.DisassembleAll(DexFile.Load(File.ReadAllBytes(path)));
    }

    /// <summary>
    /// <para>把 JAR/APK 容器的全部 classes*.dex 合并反汇编为 Smali 文本。</para>
    /// Merges and disassembles every classes*.dex entry of a JAR/APK container into Smali text.
    /// </summary>
    private static string ReadContainer(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DexContainerSearchText.BuildMergedSource(path);
    }

    /// <summary>
    /// <para>把 AXML 字节解析为 XML 文本（与 Manifest 编辑器的预览文本一致）。</para>
    /// Parses AXML bytes into XML text (identical to the Manifest editor's preview text).
    /// </summary>
    private static string ReadAxml(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = AXmlParser.Parse(File.ReadAllBytes(path));
        cancellationToken.ThrowIfCancellationRequested();
        return document.ToXml();
    }

    /// <summary>
    /// <para>超过 8 MiB 搜索文本上限时放弃该文件，避免把超长文本带入匹配通道。</para>
    /// Discards the file when the search text exceeds the 8 MiB limit, keeping oversized text out
    /// of the matching channel.
    /// </summary>
    private static string? Bound(string text) =>
        text.Length > TextSearchService.MaximumTextLength ? null : text;

    /// <summary>
    /// <para>判断文件名是否为 AndroidManifest.xml（大小写不敏感）。</para>
    /// Whether the file name is AndroidManifest.xml (case-insensitive).
    /// </summary>
    private static bool IsManifestPath(string path) =>
        Path.GetFileName(path).Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>有界文件头 {@code header} 的 DTB 魔数判定（大端 FDT 魔数）。</para>
    /// DTB magic check (big-endian FDT magic) on the bounded file header.
    /// </summary>
    private static bool IsDtbMagic(byte[] header) =>
        header.Length >= DtbHeaderLength && BinaryPrimitives.ReadUInt32BigEndian(header) == FdtConstants.FdtMagic;

    private static bool IsDexMagic(byte[] header) =>
        header.Length >= DexHeaderLength
        && header[0] == (byte)'d' && header[1] == (byte)'e' && header[2] == (byte)'x'
        && header[3] == (byte)'\n'
        && header[4] >= (byte)'0' && header[4] <= (byte)'9'
        && header[5] >= (byte)'0' && header[5] <= (byte)'9'
        && header[6] >= (byte)'0' && header[6] <= (byte)'9'
        && header[7] == 0;
}