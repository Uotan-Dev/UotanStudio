using System.Buffers.Binary;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Bsdiff;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.PuffDiff;

/// <summary>Applies standard PUF1 patches with lossless DEFLATE token transforms. <para>通过无损 DEFLATE 标记转换应用标准 PUF1 补丁。</para></summary>
public static class PuffDiffApplier
{
    private const int MaximumBufferBytes = 256 * 1024 * 1024;
    internal readonly record struct BitRange(long Offset, long Length);
    private sealed record StreamInfo(List<BitRange> Deflates, List<BitRange> Puffs, int PuffLength);
    private sealed record Header(StreamInfo Source, StreamInfo Target, int PatchOffset);

    /// <summary>Returns the inner BSDIFF stream from a PUF1 container. <para>返回 PUF1 容器内的 BSDIFF 流。</para></summary>
    public static byte[] DecompressPatch(byte[] puffDiffPatch) => DecompressPatch(puffDiffPatch.AsSpan());
    /// <summary>Returns the inner BSDIFF stream without applying Puff transforms. <para>返回内部 BSDIFF 流，不执行 Puff 转换。</para></summary>
    public static byte[] DecompressPatch(ReadOnlySpan<byte> puffDiffPatch)
    { var header = ParseHeader(puffDiffPatch); return puffDiffPatch[header.PatchOffset..].ToArray(); }
    /// <summary>Applies a PUF1 patch to a small source buffer. <para>将 PUF1 补丁应用到小型源缓冲区。</para></summary>
    public static byte[] Apply(byte[] puffDiffPatch, byte[] oldData) => Apply(puffDiffPatch.AsSpan(), oldData.AsSpan());
    /// <summary>Applies a PUF1 patch with a 256 MiB per-buffer limit. <para>按每个缓冲区 256 MiB 上限应用 PUF1 补丁。</para></summary>
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
        var output = new byte[info.PuffLength]; long previousBit = 0; int position = 0;
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
        var output = new byte[(int)rawSize]; var tokens = input.ToArray(); long previousBit = 0; int position = 0;
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