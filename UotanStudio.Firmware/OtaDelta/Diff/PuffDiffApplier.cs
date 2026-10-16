using System.Buffers.Binary;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload;

namespace UotanStudio.Firmware.OtaDelta.Diff;

/// <summary>Applies standard PUF1 patches with lossless DEFLATE token transforms. <para>通过无损 DEFLATE 标记转换应用标准 PUF1 补丁。</para></summary>
internal static class PuffDiffApplier
{
    private const int MaximumBufferBytes = 256 * 1024 * 1024;
    internal readonly record struct BitRange(long Offset, long Length);
    private sealed record StreamInfo(List<BitRange> Deflates, List<BitRange> Puffs, int PuffLength);
    private sealed record Header(StreamInfo Source, StreamInfo Target, int PatchOffset);

    /// <summary>Returns the inner BSDIFF stream from a PUF1 container. <para>返回 PUF1 容器内的 BSDIFF 流。</para></summary>
    /// <param name="puffDiffPatch">
    /// Complete PUF1 container: the <c>PUF1</c> magic, a big-endian protobuf header length and the header itself.
    /// <para>完整的 PUF1 容器：<c>PUF1</c> 魔数、大端序的 protobuf 头长度以及头部本身。</para>
    /// </param>
    /// <returns>
    /// A new array holding the bytes that follow the header, i.e. a plain BSDIFF40 patch usable with
    /// <see cref="BinaryDeltaPatch"/>; DEFLATE extents are left untouched.
    /// <para>新数组，包含头部之后的字节，即可与 <see cref="BinaryDeltaPatch"/> 配合使用的普通 BSDIFF40
    /// 补丁；DEFLATE 区间保持不变。</para>
    /// </returns>
    /// <exception cref="PayloadException">
    /// The container is too short, has the wrong magic, declares an oversized protobuf header, or omits the source
    /// or target stream info. <para>容器过短、魔数不符、声明的 protobuf 头过大，或缺少源或目标流信息。</para>
    /// </exception>
    public static byte[] DecompressPatch(byte[] puffDiffPatch) => DecompressPatch(puffDiffPatch.AsSpan());
    /// <summary>Returns the inner BSDIFF stream without applying Puff transforms. <para>返回内部 BSDIFF 流，不执行 Puff 转换。</para></summary>
    /// <param name="puffDiffPatch">
    /// Complete PUF1 container read from its start; the span may be longer than the patch it describes only in
    /// bytes after the trailing BSDIFF stream.
    /// <para>从头读取的完整 PUF1 容器；仅在其尾部 BSDIFF 流之后的字节处允许 span 比所描述补丁更长。</para>
    /// </param>
    /// <returns>
    /// A copy of every byte after the protobuf header, without any DEFLATE-to-puff expansion.
    /// <para>protobuf 头之后所有字节的副本，不含任何 DEFLATE 到 puffed 的展开。</para>
    /// </returns>
    /// <exception cref="PayloadException">
    /// The container is too short, has the wrong magic, declares an oversized protobuf header, or omits the source
    /// or target stream info. <para>容器过短、魔数不符、声明的 protobuf 头过大，或缺少源或目标流信息。</para>
    /// </exception>
    public static byte[] DecompressPatch(ReadOnlySpan<byte> puffDiffPatch)
    { var header = ParseHeader(puffDiffPatch); return puffDiffPatch[header.PatchOffset..].ToArray(); }
    /// <summary>Applies a PUF1 patch to a small source buffer. <para>将 PUF1 补丁应用到小型源缓冲区。</para></summary>
    /// <param name="puffDiffPatch">
    /// PUF1 container holding the header, the DEFLATE extents and the inner BSDIFF stream.
    /// <para>包含头部、DEFLATE 区间与内部 BSDIFF 流的 PUF1 容器。</para>
    /// </param>
    /// <param name="oldData">
    /// Source image bytes the patch transforms; must match the extents recorded in the container.
    /// <para>被补丁转换的源镜像字节；必须与容器中记录的区间一致。</para>
    /// </param>
    /// <returns>
    /// The reconstructed target image in its compressed (huffed) form; its length follows from the recorded target
    /// extents and is shorter than the puffed working size whenever a DEFLATE extent is present.
    /// <para>还原出的目标镜像，以压缩（huffed）形式给出；其长度由记录的目标区间推出，只要存在 DEFLATE 区间就会短于
    /// puffed 工作尺寸。</para>
    /// </returns>
    /// <exception cref="PayloadException">
    /// The container is malformed, a buffer exceeds <c>256</c> MiB, the extents overlap or run past the stream, or
    /// the reconstructed size differs from the declared puffed target size.
    /// <para>容器畸形、某个缓冲区超过 <c>256</c> MiB、区间互相重叠或越过流边界，或还原出的大小与声明的目标 puffed 大小不一致。</para>
    /// </exception>
    public static byte[] Apply(byte[] puffDiffPatch, byte[] oldData) => Apply(puffDiffPatch.AsSpan(), oldData.AsSpan());
    /// <summary>Applies a PUF1 patch with a 256 MiB per-buffer limit. <para>按每个缓冲区 256 MiB 上限应用 PUF1 补丁。</para></summary>
    /// <param name="puffDiffPatch">
    /// PUF1 container; its length must not exceed <c>256</c> MiB. <para>PUF1 容器；其长度不得超过 <c>256</c> MiB。</para>
    /// </param>
    /// <param name="oldData">
    /// Source image bytes, copied into a puffed working buffer whose size must also stay within <c>256</c> MiB.
    /// <para>源镜像字节，会被复制进 puffed 工作缓冲区，该缓冲区大小同样不得超过 <c>256</c> MiB。</para>
    /// </param>
    /// <returns>
    /// The huffed target image produced by applying the inner BSDIFF stream to the puffed source.
    /// <para>把内部 BSDIFF 流应用到 puffed 源后所得到的 huffed 目标镜像。</para>
    /// </returns>
    /// <exception cref="PayloadException">
    /// Any structural, size or mapping problem in the container or the result, including out-of-range gap bits.
    /// Every failure surfaces as this exception type.
    /// <para>容器或结果中的任何结构、大小或映射问题，包括越界的间隙位。所有失败都以该异常类型呈现。</para>
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A working buffer larger than the limit could not be allocated; this escapes unwrapped.
    /// <para>无法分配超过上限的工作缓冲区；该异常不会被包装。</para>
    /// </exception>
    public static byte[] Apply(ReadOnlySpan<byte> puffDiffPatch, ReadOnlySpan<byte> oldData)
    {
        try
        {
            if (oldData.Length > MaximumBufferBytes || puffDiffPatch.Length > MaximumBufferBytes) throw new InvalidDataException("PUFFDIFF buffer limit exceeded.");
            var header = ParseHeader(puffDiffPatch);
            var source = Puff(oldData.ToArray(), header.Source);
            using var old = new MemoryStream(source, false);
            using var patch = new MemoryStream(puffDiffPatch[header.PatchOffset..].ToArray(), false);
            using var result = new MemoryStream();
            BinaryDeltaPatch.Apply(old, patch, result, header.Target.PuffLength);
            if (result.Length != header.Target.PuffLength) throw new InvalidDataException("PUFFDIFF puffed target size mismatch.");
            return Huff(result.GetBuffer().AsSpan(0, checked((int)result.Length)), header.Target);
        }
        catch (Exception ex) when (ex is not PayloadException and not OutOfMemoryException)
        { throw new PayloadException($"Failed to apply standard PUFFDIFF: {ex.Message}", ex); }
    }

    /// <summary>Creates an interoperable PUF1 patch using byte extents without DEFLATE transforms. <para>生成可互操作的 PUF1 补丁，使用不进行 DEFLATE 转换的字节区间。</para></summary>
    /// <param name="oldData">
    /// Source image; its length is recorded as the source stream length and must not exceed <c>256</c> MiB.
    /// <para>源镜像；其长度记入源流长度字段，且不得超过 <c>256</c> MiB。</para>
    /// </param>
    /// <param name="newData">
    /// Target image; its length is recorded as the target stream length and must not exceed <c>256</c> MiB.
    /// <para>目标镜像；其长度记入目标流长度字段，且不得超过 <c>256</c> MiB。</para>
    /// </param>
    /// <returns>
    /// The complete container: <c>PUF1</c> magic, big-endian header length, a version-1 protobuf header carrying only
    /// stream lengths, and the BSDIFF40 stream produced from the two buffers. Applying it reconstructs
    /// <paramref name="newData"/> byte for byte.
    /// <para>完整容器：<c>PUF1</c> 魔数、大端序头长度、仅携带流长度的版本 1 protobuf 头，以及由这两个缓冲区生成的
    /// BSDIFF40 流。应用该补丁可逐字节还原 <paramref name="newData"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Either buffer exceeds <c>256</c> MiB. <para>任一缓冲区超过 <c>256</c> MiB。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="oldData"/> or <paramref name="newData"/> is <see langword="null"/>.
    /// <para><paramref name="oldData"/> 或 <paramref name="newData"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public static byte[] CreatePatch(byte[] oldData, byte[] newData)
    {
        ArgumentNullException.ThrowIfNull(oldData); ArgumentNullException.ThrowIfNull(newData);
        if (oldData.Length > MaximumBufferBytes || newData.Length > MaximumBufferBytes) throw new ArgumentException("PUFFDIFF buffer limit exceeded.");
        using var header = new MemoryStream();
        using (var proto = new CodedOutputStream(header, leaveOpen: true))
        {
            proto.WriteTag(1, WireFormat.WireType.Varint); proto.WriteInt32(1);
            foreach (var (field, size) in new[] { (2, oldData.Length), (3, newData.Length) })
            {
                using var info = new MemoryStream();
                using (var child = new CodedOutputStream(info, true)) { child.WriteTag(3, WireFormat.WireType.Varint); child.WriteUInt64((ulong)size); }
                proto.WriteTag(field, WireFormat.WireType.LengthDelimited); proto.WriteBytes(ByteString.CopyFrom(info.ToArray()));
            }
        }
        using var output = new MemoryStream(); output.Write("PUF1"u8);
        Span<byte> sizeBytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, checked((uint)header.Length)); output.Write(sizeBytes);
        output.Write(header.GetBuffer().AsSpan(0, checked((int)header.Length)));
        BinaryDeltaPatch.Create(new MemoryStream(oldData, false), new MemoryStream(newData, false), output);
        return output.ToArray();
    }

    /// <summary>Creates a standard PUF1 patch from bounded seekable streams without buffering either image as a whole.
    /// <para>从有界可定位流创建标准 PUF1 补丁，不将任一完整镜像整体载入内存。</para></summary>
    /// <param name="oldData">Seekable source image stream positioned at zero.</param>
    /// <param name="newData">Seekable target image stream positioned at zero.</param>
    /// <param name="output">Writable destination receiving the complete PUF1 patch.</param>
    /// <param name="cancellationToken">Cancellation token checked during patch generation and copying.</param>
    /// <exception cref="ArgumentException">A stream is not seekable/readable or exceeds the 256 MiB limit.</exception>
    public static void CreatePatch(Stream oldData, Stream newData, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldData);
        ArgumentNullException.ThrowIfNull(newData);
        ArgumentNullException.ThrowIfNull(output);
        if (!oldData.CanSeek || !oldData.CanRead || !newData.CanSeek || !newData.CanRead || !output.CanWrite
            || oldData.Position != 0 || newData.Position != 0)
            throw new ArgumentException("PUFFDIFF streams must be readable/seekable, with a writable output.");
        var oldLength = oldData.Length;
        var newLength = newData.Length;
        if (oldLength < 0 || newLength < 0 || oldLength > MaximumBufferBytes || newLength > MaximumBufferBytes)
            throw new ArgumentException("PUFFDIFF stream length exceeds the 256 MiB limit.");
        cancellationToken.ThrowIfCancellationRequested();
        using var header = new MemoryStream();
        using (var proto = new CodedOutputStream(header, leaveOpen: true))
        {
            proto.WriteTag(1, WireFormat.WireType.Varint);
            proto.WriteInt32(1);
            WriteStreamInfo(proto, 2, oldLength);
            WriteStreamInfo(proto, 3, newLength);
        }

        output.Write("PUF1"u8);
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, checked((uint)header.Length));
        output.Write(sizeBytes);
        output.Write(header.GetBuffer().AsSpan(0, checked((int)header.Length)));
        cancellationToken.ThrowIfCancellationRequested();
        BinaryDeltaPatch.Create(oldData, newData, output, cancellationToken);
    }

    private static void WriteStreamInfo(CodedOutputStream output, int field, long length)
    {
        using var info = new MemoryStream();
        using (var child = new CodedOutputStream(info, leaveOpen: true))
        {
            child.WriteTag(3, WireFormat.WireType.Varint);
            child.WriteUInt64(checked((ulong)length));
        }
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(info.GetBuffer(), 0, checked((int)info.Length)));
    }

    private static Header ParseHeader(ReadOnlySpan<byte> patch)
    {
        if (patch.Length < 8 || !patch[..4].SequenceEqual("PUF1"u8)) throw new PayloadException("Invalid PUF1 patch magic.");
        var size = BinaryPrimitives.ReadUInt32BigEndian(patch[4..8]);
        if (size > 16 * 1024 * 1024 || size > patch.Length - 8) throw new PayloadException("Invalid PUF1 protobuf size.");
        using var input = new CodedInputStream(patch.Slice(8, (int)size).ToArray());
        StreamInfo? source = null, target = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8: if (input.ReadInt32() != 1) throw new PayloadException("Unsupported PUF1 version."); break;
                case 18: source = ParseInfo(input.ReadBytes().ToByteArray()); break;
                case 26: target = ParseInfo(input.ReadBytes().ToByteArray()); break;
                default: input.SkipLastField(); break;
            }
        }
        return new(source ?? throw new PayloadException("Missing PUF1 source info."), target ?? throw new PayloadException("Missing PUF1 target info."), checked(8 + (int)size));
    }
    private static StreamInfo ParseInfo(byte[] bytes)
    {
        using var input = new CodedInputStream(bytes); var deflates = new List<BitRange>(); var puffs = new List<BitRange>(); long length = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag is 10 or 18)
            {
                using var range = new CodedInputStream(input.ReadBytes().ToByteArray()); long offset = 0, size = 0; uint rangeTag;
                while ((rangeTag = range.ReadTag()) != 0)
                { if (rangeTag == 8) offset = checked((long)range.ReadUInt64()); else if (rangeTag == 16) size = checked((long)range.ReadUInt64()); else range.SkipLastField(); }
                (tag == 10 ? deflates : puffs).Add(new(offset, size));
            }
            else if (tag == 24) length = checked((long)input.ReadUInt64()); else input.SkipLastField();
        }
        if (length > MaximumBufferBytes || deflates.Count != puffs.Count) throw new InvalidDataException("Invalid PUF1 extent counts or length.");
        long endDeflate = 0, endPuff = 0;
        for (var i = 0; i < deflates.Count; i++)
        {
            var d = deflates[i]; var p = puffs[i];
            if (d.Offset < endDeflate || d.Length <= 0 || p.Offset < endPuff || p.Length <= 0 || (p.Offset & 7) != 0 || (p.Length & 7) != 0)
                throw new InvalidDataException("Invalid or overlapping PUF1 extents.");
            endDeflate = checked(d.Offset + d.Length); endPuff = checked(p.Offset + p.Length);
            if (endPuff / 8 > length) throw new InvalidDataException("PUF1 extent exceeds puffed stream length.");
        }
        return new(deflates, puffs, checked((int)length));
    }
    private static byte[] Puff(byte[] input, StreamInfo info)
    {
        var output = new byte[info.PuffLength]; long previousBit = 0; var position = 0;
        for (var i = 0; i <= info.Deflates.Count; i++)
        {
            var nextBit = i == info.Deflates.Count ? (long)input.Length * 8 : info.Deflates[i].Offset;
            if (nextBit > (long)input.Length * 8 || nextBit < previousBit) throw new InvalidDataException("PUF1 source extent exceeds input.");
            CopyGap(input, output, ref position, previousBit, nextBit, huff: false);
            if (i == info.Deflates.Count) break;
            var puff = info.Puffs[i]; var deflate = info.Deflates[i];
            if (position != puff.Offset / 8) throw new InvalidDataException("PUF1 source gap mapping mismatch.");
            var tokens = PuffDeflateCodec.Puff(input, deflate.Offset, deflate.Length, checked((int)(puff.Length / 8)));
            tokens.CopyTo(output, position); position += tokens.Length; previousBit = checked(deflate.Offset + deflate.Length);
        }
        if (position != output.Length) throw new InvalidDataException("PUF1 source size mismatch.");
        return output;
    }
    private static byte[] Huff(ReadOnlySpan<byte> input, StreamInfo info)
    {
        var rawSize = info.Deflates.Count == 0 ? info.PuffLength : checked((info.Deflates[^1].Offset + info.Deflates[^1].Length) / 8 + info.PuffLength - (info.Puffs[^1].Offset + info.Puffs[^1].Length) / 8);
        if (rawSize < 0 || rawSize > MaximumBufferBytes) throw new InvalidDataException("PUF1 target exceeds the output limit.");
        var output = new byte[(int)rawSize]; var tokens = input.ToArray(); long previousBit = 0; var position = 0;
        for (var i = 0; i <= info.Deflates.Count; i++)
        {
            var nextBit = i == info.Deflates.Count ? rawSize * 8 : info.Deflates[i].Offset;
            if (nextBit < previousBit || nextBit > rawSize * 8) throw new InvalidDataException("Invalid PUF1 target extent.");
            CopyGap(output, tokens, ref position, previousBit, nextBit, huff: true);
            if (i == info.Deflates.Count) break;
            var puff = info.Puffs[i]; var deflate = info.Deflates[i];
            if (position != puff.Offset / 8) throw new InvalidDataException("PUF1 target gap mapping mismatch.");
            PuffDeflateCodec.Huff(input.Slice(position, checked((int)(puff.Length / 8))), output, deflate.Offset, deflate.Length);
            position += checked((int)(puff.Length / 8)); previousBit = checked(deflate.Offset + deflate.Length);
        }
        if (position != input.Length) throw new InvalidDataException("PUF1 target size mismatch.");
        return output;
    }
    private static void CopyGap(byte[] raw, byte[] puffed, ref int position, long begin, long end, bool huff)
    {
        if (begin == end) return;
        for (var byteIndex = begin / 8; byteIndex < (end + 7) / 8; byteIndex++)
        {
            if (position >= puffed.Length) throw new InvalidDataException("PUF1 gap exceeds puffed data.");
            var low = (int)Math.Max(0, begin - byteIndex * 8); var high = (int)Math.Min(8, end - byteIndex * 8);
            var mask = (1 << (high - low)) - 1;
            if (huff)
            {
                if ((puffed[position] & ~mask) != 0) throw new InvalidDataException("PUF1 gap contains out-of-range bits.");
                raw[byteIndex] |= (byte)(puffed[position] << low);
            }
            else puffed[position] = (byte)((raw[byteIndex] >> low) & mask);
            position++;
        }
    }
}