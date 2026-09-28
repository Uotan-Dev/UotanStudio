using System.Buffers.Binary;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Shared skeleton for format-handler sniffing: open the file, require a minimum length, read a magic at a fixed
/// offset and compare it, swallowing every error as a negative probe result.
/// <para>格式嗅探的公共骨架：打开文件、要求最小长度、读取固定偏移处的魔数并比较，任何错误都按探测失败处理。</para>
/// </summary>
internal static class HeaderProbe
{
    /// <summary>
    /// Probes a file for an exact byte magic at the given offset.
    /// <para>探测文件在给定偏移处是否为精确字节魔数。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to test; <see langword="null"/>, blank, missing or unreadable paths yield <see langword="false"/>.
    /// <para>待测试路径；<see langword="null"/>、空白、不存在或不可读均返回 <see langword="false"/>。</para>
    /// </param>
    /// <param name="minimumLength">
    /// Minimum total file length in bytes; use <c>0</c> when only the magic window matters.
    /// <para>文件最小总长度（字节）；只关心魔数窗口时用 <c>0</c>。</para>
    /// </param>
    /// <param name="offset">
    /// Byte offset of the magic inside the file.
    /// <para>魔数在文件内的字节偏移。</para>
    /// </param>
    /// <param name="magic">Exact byte sequence compared at <paramref name="offset"/>. <para>在 <paramref name="offset"/> 处比较的精确字节序列。</para></param>
    /// <returns>
    /// <see langword="true"/> only when the file is long enough and carries the magic; any read error yields
    /// <see langword="false"/>. <para>仅当文件足够长且携带魔数时为 <see langword="true"/>；任何读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool ProbeMagic(string? filePath, int minimumLength, int offset, ReadOnlySpan<byte> magic)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || magic.IsEmpty)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return Probe(stream, minimumLength, offset, magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Probes a file for a little-endian unsigned integer magic at the given offset.
    /// <para>探测文件在给定偏移处是否为小端无符号整数魔数。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to test; <see langword="null"/>, blank, missing or unreadable paths yield <see langword="false"/>.
    /// <para>待测试路径；<see langword="null"/>、空白、不存在或不可读均返回 <see langword="false"/>。</para>
    /// </param>
    /// <param name="minimumLength">
    /// Minimum total file length in bytes; use <c>0</c> when only the magic window matters.
    /// <para>文件最小总长度（字节）；只关心魔数窗口时用 <c>0</c>。</para>
    /// </param>
    /// <param name="offset">
    /// Byte offset of the magic inside the file.
    /// <para>魔数在文件内的字节偏移。</para>
    /// </param>
    /// <param name="magic">Value compared in little-endian encoding at <paramref name="offset"/>. <para>在 <paramref name="offset"/> 处按小端编码比较的值。</para></param>
    /// <returns>
    /// <see langword="true"/> only when the file is long enough and carries the magic; any read error yields
    /// <see langword="false"/>. <para>仅当文件足够长且携带魔数时为 <see langword="true"/>；任何读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool ProbeMagic(string? filePath, int minimumLength, int offset, uint magic)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
        return ProbeMagic(filePath, minimumLength, offset, bytes);
    }

    private static bool Probe(Stream stream, int minimumLength, int offset, ReadOnlySpan<byte> magic)
    {
        if (stream.Length < minimumLength || stream.Length - offset < magic.Length)
        {
            return false;
        }

        stream.Position = offset;
        Span<byte> bytes = stackalloc byte[magic.Length];
        stream.ReadExactly(bytes);
        return bytes.SequenceEqual(magic);
    }
}