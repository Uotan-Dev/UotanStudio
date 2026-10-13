using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Arsc;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using UotanStudio.Firmware.DeviceTree.Dt;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// Bounded text-ification of unopened project binary formats (DTB, ARSC, AXML, DEX/JAR) for
/// project-scope text search; unrecognized or oversized files are skipped.
/// <para>把项目内未打开的二进制格式（DTB、ARSC、AXML、DEX/JAR）有界地文本化，用于项目范围的文本搜索；无法识别或超限的文件按跳过处理。</para>
/// </summary>
internal static class FormatSearchText
{
    /// <summary>
    /// Length of the single header read: the longest magic-check length (ARSC table type + header
    /// size + file size); AXML (8) and DEX (8) checks reuse the same header.
    /// <para>一次读取的文件头长度：取最长魔数判定的长度（ARSC 表头类型 + 头部大小 + 文件大小），
    /// AXML（8）与 DEX（8）判定复用同一头部。</para>
    /// </summary>
    private const int ArscHeaderLength = 12;

    /// <summary>
    /// Header length needed for DEX magic detection ("dex\\n" + version + NUL).
    /// <para>DEX 魔数判定所需的头部长度（"dex\\n" + 版本 + NUL）。</para>
    /// </summary>
    private const int DexHeaderLength = 8;

    /// <summary>
    /// Header length needed for DTB magic detection (big-endian FDT magic).
    /// <para>DTB 魔数判定所需的头部长度（大端 FDT 魔数）。</para>
    /// </summary>
    private const int DtbHeaderLength = 4;

    /// <summary>
    /// Identifies and text-ifies a project file by extension/magic; <c>null</c> when unrecognized or
    /// over-limit.
    /// <para>按扩展名/魔数识别并文本化项目文件；无法识别或超限时为 <c>null</c>。</para>
    /// </summary>
    /// <param name="path">
    /// The project file path.
    /// <para>项目文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The token checked before and during parsing.
    /// <para>解析前后均检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The searchable text, or <c>null</c> when the file is skipped.
    /// <para>可搜索文本，跳过该文件时为 <c>null</c>。</para>
    /// </returns>
    internal static string? TryRead(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > TextSearchService.MaximumTextLength)
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        byte[] header;
        try
        {
            header = UotanStudio.Firmware.Core.FormatDetector.ReadHeader(path, ArscHeaderLength);
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
            return null;
        }

        return null;
    }

    /// <summary>
    /// Parses DTB bytes into DTS text.
    /// <para>把 DTB 字节解析为 DTS 文本。</para>
    /// </summary>
    /// <param name="path">
    /// The DTB file path.
    /// <para>DTB 文件路径。</para>
    /// </param>
    /// <returns>
    /// The rendered DTS text.
    /// <para>渲染出的 DTS 文本。</para>
    /// </returns>
    private static string ReadDtb(string path) => DtsFormat.Write(DtbFormat.Read(File.ReadAllBytes(path)));

    /// <summary>
    /// Parses ARSC bytes into resource-table search text.
    /// <para>把 ARSC 字节解析为资源表搜索文本。</para>
    /// </summary>
    /// <param name="path">
    /// The ARSC file path.
    /// <para>ARSC 文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The token checked around parsing.
    /// <para>解析过程两侧检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The serialized resource-table text.
    /// <para>序列化后的资源表文本。</para>
    /// </returns>
    private static string ReadArsc(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var table = ArscParser.Parse(File.ReadAllBytes(path));
        cancellationToken.ThrowIfCancellationRequested();
        return ResourceTableSearchText.Serialize(table);
    }

    /// <summary>
    /// Disassembles plain DEX bytes into Smali text.
    /// <para>把普通 DEX 字节反汇编为 Smali 文本。</para>
    /// </summary>
    /// <param name="path">
    /// The DEX file path.
    /// <para>DEX 文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The token checked before disassembly.
    /// <para>反汇编前检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The disassembled Smali text.
    /// <para>反汇编得到的 Smali 文本。</para>
    /// </returns>
    private static string ReadDex(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Disassembler.DisassembleAll(DexFile.Load(File.ReadAllBytes(path)));
    }

    /// <summary>
    /// Merges and disassembles every classes*.dex entry of a JAR/APK container into Smali text.
    /// <para>把 JAR/APK 容器的全部 classes*.dex 条目合并反汇编为 Smali 文本。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The token checked before merging.
    /// <para>合并前检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The merged Smali text.
    /// <para>合并后的 Smali 文本。</para>
    /// </returns>
    private static string ReadContainer(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DexContainerSearchText.BuildMergedSource(path);
    }

    /// <summary>
    /// Parses AXML bytes into XML text.
    /// <para>把 AXML 字节解析为 XML 文本。</para>
    /// </summary>
    /// <param name="path">
    /// The AXML file path.
    /// <para>AXML 文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The token checked around parsing.
    /// <para>解析过程两侧检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The rendered XML text.
    /// <para>渲染出的 XML 文本。</para>
    /// </returns>
    private static string ReadAxml(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = AXmlParser.Parse(File.ReadAllBytes(path));
        cancellationToken.ThrowIfCancellationRequested();
        return document.ToXml();
    }

    /// <summary>
    /// Discards the file when the search text exceeds the 8 MiB limit, keeping oversized text out
    /// of the matching channel.
    /// <para>超过 8 MiB 搜索文本上限时放弃该文件，避免把超长文本带入匹配通道。</para>
    /// </summary>
    /// <param name="text">
    /// The text produced for a candidate file.
    /// <para>为候选文件生成的文本。</para>
    /// </param>
    /// <returns>
    /// The text when within the limit, or <c>null</c>.
    /// <para>未超限时返回该文本，否则为 <c>null</c>。</para>
    /// </returns>
    private static string? Bound(string text) =>
        text.Length > TextSearchService.MaximumTextLength ? null : text;

    /// <summary>
    /// Whether the file name is AndroidManifest.xml (case-insensitive).
    /// <para>判断文件名是否为 AndroidManifest.xml（大小写不敏感）。</para>
    /// </summary>
    /// <param name="path">
    /// The project file path.
    /// <para>项目文件路径。</para>
    /// </param>
    /// <returns>
    /// True when the file name matches.
    /// <para>文件名匹配时为真。</para>
    /// </returns>
    private static bool IsManifestPath(string path) =>
        Path.GetFileName(path).Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DTB magic check (big-endian FDT magic) on the bounded file header.
    /// <para>有界文件头的 DTB 魔数判定（大端 FDT 魔数）。</para>
    /// </summary>
    /// <param name="header">
    /// The bounded header bytes read from the file.
    /// <para>从文件读取的有界头部字节。</para>
    /// </param>
    /// <returns>
    /// True when the header starts with the FDT magic.
    /// <para>头部以 FDT 魔数开头时为真。</para>
    /// </returns>
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