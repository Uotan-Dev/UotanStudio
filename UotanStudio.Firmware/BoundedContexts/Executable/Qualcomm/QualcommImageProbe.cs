using UotanStudio.Firmware.BoundedContexts.Executable.Elf;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;

/// <summary>Recognizes Qualcomm ELF metadata without hashing payloads or accessing devices.
/// <para>识别 Qualcomm ELF 元数据，不计算载荷摘要或访问设备。</para></summary>
public static class QualcommImageProbe
{
    /// <summary>Checks for a bounded ELF containing exactly one structurally valid MI_PBT hash segment.
    /// <para>检查有界 ELF 是否包含恰好一个结构有效的 MI_PBT 哈希段。</para></summary>
    /// <param name="stream">Readable seekable stream; its position is restored. <para>可读可寻址流；恢复原位置。</para></param>
    /// <param name="cancellationToken">Cancels metadata reads. <para>取消元数据读取。</para></param>
    /// <returns>True for recognized Qualcomm structure, not verified signatures.
    /// <para>已识别 Qualcomm 结构时为真，不代表签名通过。</para></returns>
    public static bool IsQualcommElf(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("A readable, seekable stream is required.", nameof(stream));
        }
        var position = stream.Position;
        try
        {
            var elf = ElfReader.Read(stream, cancellationToken);
            if (!elf.Header.IsLittleEndian)
            {
                return false;
            }
            var segments = elf.Segments.Where(segment => (segment.Flags >> 24 & 7) == 2).Take(2).ToArray();
            if (segments.Length != 1 || segments[0].FileSize is < 40 or > 64 * 1024 * 1024
                || segments[0].Offset > (ulong)stream.Length
                || segments[0].FileSize > (ulong)stream.Length - segments[0].Offset)
            {
                return false;
            }
            _ = QualcommMbnLayout.Read(stream, (long)segments[0].Offset, (long)segments[0].FileSize, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            stream.Position = position;
        }
    }
}