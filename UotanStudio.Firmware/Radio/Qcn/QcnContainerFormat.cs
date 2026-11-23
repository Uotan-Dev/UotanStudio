using System.Text;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Physical container encodings accepted by the QCN reader and writer.
/// <para>QCN 读写器接受的物理容器编码。</para>
/// </summary>
internal enum QcnContainerFormat
{
    /// <summary>Binary OLE2/CFB compound document. <para>二进制 OLE2/CFB 复合文档。</para></summary>
    Binary,

    /// <summary>QPST XML export whose streams are hex-encoded attribute values.
    /// <para>流以十六进制属性值编码的 QPST XML 导出。</para></summary>
    Xml
}

/// <summary>
/// Sniffs the leading bytes of a QCN to choose the binary compound-file or the XML parser without
/// loading the whole input.
/// <para>通过 QCN 起始字节选择二进制复合文档解析器或 XML 解析器，无需加载整个输入。</para>
/// </summary>
internal static class QcnFormatDetector
{
    private static readonly byte[] CfbSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    /// Detects the container encoding from a bounded leading sample. Unknown content is reported as
    /// <see cref="QcnContainerFormat.Binary"/> so the compound-file reader produces the precise
    /// signature error.
    /// <para>从有界起始样本判定容器编码。未知内容按 <see cref="QcnContainerFormat.Binary"/> 处理，
    /// 由复合文档读取器给出精确的魔数错误。</para></summary>
    /// <param name="path">QCN file path.<para>QCN 文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The detected container format.<para>检测到的容器格式。</para></returns>
    public static async Task<QcnContainerFormat> DetectAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        var span = header.AsSpan(0, read);
        if (span.StartsWith(CfbSignature))
        {
            return QcnContainerFormat.Binary;
        }

        var index = 0;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            index = 3;
        }
        else if (span.Length >= 2 && ((span[0] == 0xFF && span[1] == 0xFE) || (span[0] == 0xFE && span[1] == 0xFF)))
        {
            index = 2;
        }

        while (index < span.Length && span[index] is 0x20 or 0x09 or 0x0A or 0x0D)
        {
            index++;
        }

        var preview = Encoding.ASCII.GetString(span[index..]);
        return preview.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || preview.StartsWith("<Storage", StringComparison.OrdinalIgnoreCase)
                ? QcnContainerFormat.Xml
                : QcnContainerFormat.Binary;
    }
}