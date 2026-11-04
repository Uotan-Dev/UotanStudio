using UotanStudio.Firmware.Executable.Elf;

namespace UotanStudio.Firmware.Executable.Qualcomm;

internal static class QualcommMelfComponents
{
    private const int MaximumComponents = 256;
    private const int ScanChunkBytes = 64 * 1024;

    /// <summary>
    /// Finds the ELF components of one multi-ELF container with a bounded scan over the whole stream.
    /// <para>以有界扫描在整个流中找出一个多 ELF 容器的各个 ELF 组件。</para>
    /// </summary>
    /// <param name="stream">Readable seekable container; its position is not preserved.
    /// <para>可读可寻址的容器；不保留其位置。</para></param>
    /// <param name="token">Cancels the scan between chunks and between component headers.
    /// <para>在数据块之间与组件头之间取消扫描。</para></param>
    /// <param name="minimumComponentCount">How many components the stream must hold before any are returned, so a
    /// caller that treats a second component as the proof of a container passes two and a caller that only wants
    /// every ELF found passes one. <para>流中至少要有多少个组件才返回任何组件，使把第二个组件视为容器证据的调用方传
    /// 二，而只想得到全部 ELF 的调用方传一。</para></param>
    /// <returns>The components in file order, empty when fewer than the requested number was found.
    /// <para>按文件顺序排列的组件；找到的数量少于要求时为空。</para></returns>
    /// <exception cref="ExecutableCorruptedException">More than the supported component count was found.
    /// <para>找到的组件数超过支持上限。</para></exception>
    internal static IReadOnlyList<QualcommMelfComponent> Scan(Stream stream, CancellationToken token,
        int minimumComponentCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumComponentCount, 1);

        var offsets = new List<long>();
        var buffer = new byte[ScanChunkBytes + 3];
        long position = 0;
        while (position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = position;
            var count = stream.Read(buffer, 0, (int)Math.Min(ScanChunkBytes, stream.Length - position));
            if (count == 0)
                break;
            for (var index = 0; index <= count - 4; index++)
            {
                if (!buffer.AsSpan(index, 4).SequenceEqual("\u007fELF"u8))
                    continue;
                var offset = position + index;
                if (offsets.Count == 0 || offsets[^1] != offset)
                    offsets.Add(offset);
                if (offsets.Count > MaximumComponents)
                    throw new ExecutableCorruptedException("Qualcomm multi-ELF component count exceeds the supported limit.");
            }

            if (count < ScanChunkBytes)
                break;
            position += count - 3;
        }

        var components = new List<QualcommMelfComponent>();
        for (var index = 0; index < offsets.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var offset = offsets[index];
            var next = index + 1 < offsets.Count ? offsets[index + 1] : stream.Length;
            if (next <= offset || next - offset < 52)
            {
                continue;
            }
            stream.Position = offset;
            var headerBytes = new byte[64];
            var read = stream.Read(headerBytes, 0, Math.Min(headerBytes.Length, checked((int)Math.Min(64, next - offset))));
            if (!ElfReader.TryReadHeader(headerBytes.AsSpan(0, read), out var header)
                || header is null
                || header.ProgramHeaderOffset > (ulong)(next - offset)
                || header.ProgramHeaderCount > 0
                    && checked(header.ProgramHeaderOffset + (ulong)header.ProgramHeaderEntrySize * header.ProgramHeaderCount)
                        > (ulong)(next - offset))
            {
                continue;
            }

            components.Add(new(offset, next - offset, header.Is64Bit, header.Machine));
        }

        return components.Count >= minimumComponentCount ? components : [];
    }
}