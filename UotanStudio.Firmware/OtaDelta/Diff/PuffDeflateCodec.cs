using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.OtaDelta.Diff;

internal static class PuffDeflateCodec
{
    private static readonly int[] Order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly int[] LengthBases = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBases = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    internal static byte[] Puff(byte[] input, long offset, long length, int expectedLength)
    {
        var bits = new Bits(input, offset, checked(offset + length));
        if (expectedLength is < 0 or > 256 * 1024 * 1024) throw new InvalidDataException("Puff extent exceeds the buffer limit.");
        using var result = new MemoryStream();
        using var output = new CountingWriteStream(result, expectedLength, leaveOpen: true);
        var literals = new List<byte>(65663);
        void FlushLiterals()
        {
            if (literals.Count == 0) return;
            if (literals.Count <= 127) output.WriteByte((byte)(literals.Count - 1));
            else { output.WriteByte(127); WriteBig16(output, literals.Count - 128); }
            foreach (var value in literals) output.WriteByte(value);
            literals.Clear();
        }
        while (bits.Position < bits.End)
        {
            var final = bits.Read(1); var type = bits.Read(2);
            List<byte> metadata = [(byte)((final << 7) | (type << 5))];
            Huffman literalTree; Huffman distanceTree;
            if (type == 0)
            {
                metadata[0] |= (byte)bits.Read((int)((8 - (bits.Position & 7)) & 7));
                var count = bits.Read(16);
                if ((count ^ bits.Read(16)) != 65535) throw new InvalidDataException("Invalid stored DEFLATE length.");
                WriteBig16(output, 0); output.WriteByte(metadata[0]);
                for (var i = 0; i < count; i++) literals.Add((byte)bits.Read(8));
                FlushLiterals(); output.Write([255, 129]);
                continue;
            }
            if (type == 1) (literalTree, distanceTree) = FixedTrees();
            else if (type == 2) (literalTree, distanceTree) = ReadDynamic(bits, metadata);
            else throw new InvalidDataException("Reserved DEFLATE block type.");
            WriteBig16(output, metadata.Count - 1); foreach (var value in metadata) output.WriteByte(value);
            while (true)
            {
                var symbol = literalTree.Read(bits);
                if (symbol < 256)
                {
                    literals.Add((byte)symbol); if (literals.Count == 65663) FlushLiterals();
                }
                else if (symbol == 256) { FlushLiterals(); output.Write([255, 129]); break; }
                else
                {
                    if (symbol > 285) throw new InvalidDataException("Invalid DEFLATE length symbol.");
                    FlushLiterals();
                    var matchLength = LengthBases[symbol - 257] + bits.Read(LengthBits[symbol - 257]);
                    var distanceSymbol = distanceTree.Read(bits);
                    if (distanceSymbol >= 30) throw new InvalidDataException("Invalid DEFLATE distance symbol.");
                    var distance = DistanceBases[distanceSymbol] + bits.Read(DistanceBits[distanceSymbol]);
                    if (matchLength < 130) output.WriteByte((byte)(128 | (matchLength - 3)));
                    else { output.WriteByte(255); output.WriteByte((byte)(matchLength - 130)); }
                    WriteBig16(output, distance - 1);
                }
            }
        }
        if (bits.Position != bits.End || output.BytesWritten != expectedLength) throw new InvalidDataException("Puff extent length mismatch.");
        return result.ToArray();
    }

    internal static void Huff(ReadOnlySpan<byte> puff, byte[] output, long offset, long length)
    {
        var bits = new Bits(output, offset, checked(offset + length));
        var reader = new PuffReader(puff);
        while (reader.Remaining > 0)
        {
            var metadata = reader.Take(reader.Big16() + 1);
            var header = metadata[0]; var type = (header >> 5) & 3;
            bits.Write(1, header >> 7); bits.Write(2, type);
            if (type == 0)
            {
                bits.Write((int)((8 - (bits.Position & 7)) & 7), header & 31);
                var (kind, count, _) = reader.Token();
                if (kind == 2) { bits.Write(16, 0); bits.Write(16, 65535); }
                else
                {
                    if (kind != 0 || count > 65535) throw new InvalidDataException("Invalid stored Puff block.");
                    bits.Write(16, count); bits.Write(16, count ^ 65535);
                    foreach (var value in reader.Take(count)) bits.Write(8, value);
                    if (reader.Token().Kind != 2) throw new InvalidDataException("Missing stored Puff block end.");
                }
                continue;
            }
            Huffman literals; Huffman distances;
            if (type == 1)
            {
                if (metadata.Length != 1) throw new InvalidDataException("Invalid fixed Puff metadata.");
                (literals, distances) = FixedTrees();
            }
            else if (type == 2) (literals, distances) = WriteDynamic(bits, metadata[1..]);
            else throw new InvalidDataException("Reserved Puff block type.");
            while (true)
            {
                var token = reader.Token();
                if (token.Kind == 2) { literals.Write(bits, 256); break; }
                if (token.Kind == 0) { foreach (var value in reader.Take(token.Length)) literals.Write(bits, value); }
                else
                {
                    var lengthCode = FindBase(LengthBases, token.Length);
                    literals.Write(bits, 257 + lengthCode); bits.Write(LengthBits[lengthCode], token.Length - LengthBases[lengthCode]);
                    var distanceCode = FindBase(DistanceBases, token.Distance);
                    distances.Write(bits, distanceCode); bits.Write(DistanceBits[distanceCode], token.Distance - DistanceBases[distanceCode]);
                }
            }
        }
        if (bits.Position != bits.End) throw new InvalidDataException("Huffed DEFLATE length does not match its extent.");
    }

    private static (Huffman, Huffman) ReadDynamic(Bits bits, List<byte> metadata)
    {
        var literalCount = bits.Read(5) + 257; var distanceCount = bits.Read(5) + 1; var codeCount = bits.Read(4) + 4;
        if (literalCount > 286 || distanceCount > 30) throw new InvalidDataException("Invalid dynamic DEFLATE counts.");
        metadata.Add((byte)(literalCount - 257)); metadata.Add((byte)(distanceCount - 1)); metadata.Add((byte)(codeCount - 4));
        var codes = new byte[19];
        for (var i = 0; i < codeCount; i++)
        {
            codes[Order[i]] = (byte)bits.Read(3);
            if ((i & 1) == 0) metadata.Add((byte)(codes[Order[i]] << 4)); else metadata[^1] |= codes[Order[i]];
        }
        var codeTree = new Huffman(codes); var lengths = new List<byte>();
        while (lengths.Count < literalCount + distanceCount)
        {
            var code = codeTree.Read(bits);
            if (code < 16) { lengths.Add((byte)code); metadata.Add((byte)code); }
            else
            {
                var extra = bits.Read(code == 16 ? 2 : code == 17 ? 3 : code == 18 ? 7 : throw new InvalidDataException("Invalid DEFLATE code-length symbol."));
                var packed = (byte)((code == 16 ? 16 : code == 17 ? 20 : 28) + extra);
                metadata.Add(packed); ExpandLength(lengths, packed);
            }
        }
        if (lengths.Count != literalCount + distanceCount) throw new InvalidDataException("DEFLATE code lengths exceed declared counts.");
        return (new Huffman([.. lengths.Take(literalCount)]), new Huffman([.. lengths.Skip(literalCount)]));
    }

    private static (Huffman, Huffman) WriteDynamic(Bits bits, ReadOnlySpan<byte> metadata)
    {
        var reader = new PuffReader(metadata);
        var counts = reader.Take(3);
        var literalCount = counts[0] + 257; var distanceCount = counts[1] + 1; var codeCount = counts[2] + 4;
        if (literalCount > 286 || distanceCount > 30 || codeCount > 19) throw new InvalidDataException("Invalid Puff Huffman counts.");
        bits.Write(5, counts[0]); bits.Write(5, counts[1]); bits.Write(4, counts[2]);
        var packed = reader.Take((codeCount + 1) / 2); var codes = new byte[19];
        for (var i = 0; i < codeCount; i++)
        { codes[Order[i]] = (byte)(((i & 1) == 0 ? packed[i / 2] >> 4 : packed[i / 2]) & 15); if (codes[Order[i]] > 7) throw new InvalidDataException("Invalid Puff code length."); bits.Write(3, codes[Order[i]]); }
        var codeTree = new Huffman(codes); var lengths = new List<byte>();
        while (lengths.Count < literalCount + distanceCount)
        {
            var value = reader.Take(1)[0];
            var code = value < 16 ? value : value < 20 ? 16 : value < 28 ? 17 : 18;
            codeTree.Write(bits, code);
            if (code < 16) lengths.Add(value);
            else { bits.Write(code == 16 ? 2 : code == 17 ? 3 : 7, value - (code == 16 ? 16 : code == 17 ? 20 : 28)); ExpandLength(lengths, value); }
        }
        if (lengths.Count != literalCount + distanceCount || reader.Remaining != 0) throw new InvalidDataException("Invalid Puff Huffman metadata length.");
        return (new Huffman([.. lengths.Take(literalCount)]), new Huffman([.. lengths.Skip(literalCount)]));
    }

    private static void ExpandLength(List<byte> lengths, byte packed)
    {
        if (packed > 155 || (packed < 20 && lengths.Count == 0)) throw new InvalidDataException("Invalid Puff code repeat.");
        var count = packed < 20 ? packed - 16 + 3 : packed < 28 ? packed - 20 + 3 : packed - 28 + 11;
        var value = packed < 20 ? lengths[^1] : (byte)0;
        for (var i = 0; i < count; i++) lengths.Add(value);
    }
    private static int FindBase(int[] values, int value)
    { var i = 0; while (i + 1 < values.Length && values[i + 1] <= value) i++; return i; }
    private static void WriteBig16(Stream output, int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value)); output.Write(bytes); }
    private static (Huffman, Huffman) FixedTrees()
    {
        var literals = new byte[288];
        for (var i = 0; i < literals.Length; i++) literals[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        return (new Huffman(literals), new Huffman([.. Enumerable.Repeat((byte)5, 32)]));
    }

    private sealed class Huffman
    {
        private readonly byte[] _lengths;
        private readonly int[] _codes;
        private readonly Dictionary<int, int> _symbols = [];
        internal Huffman(byte[] lengths)
        {
            _lengths = lengths; _codes = new int[lengths.Length]; var counts = new int[16]; var next = new int[16];
            foreach (var length in lengths) { if (length > 15) throw new InvalidDataException("Invalid Huffman length."); counts[length]++; }
            counts[0] = 0; int code = 0;
            for (var length = 1; length <= 15; length++)
            { code = (code + counts[length - 1]) << 1; next[length] = code; if (code + counts[length] > 1 << length) throw new InvalidDataException("Oversubscribed Huffman tree."); }
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol]; if (length == 0) continue;
                var canonical = next[length]++; int reversed = 0;
                for (var bit = 0; bit < length; bit++) { reversed = (reversed << 1) | (canonical & 1); canonical >>= 1; }
                _codes[symbol] = reversed; _symbols.Add((length << 16) | reversed, symbol);
            }
        }
        internal int Read(Bits bits)
        {
            int code = 0;
            for (var length = 1; length <= 15; length++)
            { code |= bits.Read(1) << (length - 1); if (_symbols.TryGetValue((length << 16) | code, out var symbol)) return symbol; }
            throw new InvalidDataException("Invalid Huffman symbol.");
        }
        internal void Write(Bits bits, int symbol)
        { if (symbol < 0 || symbol >= _lengths.Length || _lengths[symbol] == 0) throw new InvalidDataException("Unavailable Huffman symbol."); bits.Write(_lengths[symbol], _codes[symbol]); }
    }
    private sealed class Bits(byte[] data, long offset, long end)
    {
        internal long Position { get; private set; } = offset;
        internal long End => end;
        internal int Read(int count)
        {
            if (count < 0 || count > 16 || Position < 0 || end > (long)data.Length * 8 || count > end - Position) throw new InvalidDataException("Truncated DEFLATE bits.");
            int result = 0;
            for (var i = 0; i < count; i++, Position++) result |= ((data[Position / 8] >> (int)(Position & 7)) & 1) << i;
            return result;
        }
        internal void Write(int count, int value)
        {
            if (count < 0 || count > 16 || Position < 0 || end > (long)data.Length * 8 || count > end - Position) throw new InvalidDataException("Huffed DEFLATE exceeds its extent.");
            for (var i = 0; i < count; i++, Position++)
            { var mask = (byte)(1 << (int)(Position & 7)); data[Position / 8] = (byte)((data[Position / 8] & ~mask) | (((value >> i) & 1) != 0 ? mask : 0)); }
        }
    }
    private ref struct PuffReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;
        internal int Remaining => _data.Length;
        internal ReadOnlySpan<byte> Take(int count) { if (count < 0 || count > _data.Length) throw new InvalidDataException("Truncated Puff stream."); var result = _data[..count]; _data = _data[count..]; return result; }
        internal int Big16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal (int Kind, int Length, int Distance) Token()
        {
            var token = Take(1)[0];
            if (token < 128) return (0, token == 127 ? Big16() + 128 : token + 1, 0);
            var length = (token & 127) + 3; if (length == 130) length += Take(1)[0];
            if (length == 259) return (2, 0, 0);
            if (length > 258) throw new InvalidDataException("Invalid Puff match length.");
            var distance = Big16() + 1; if (distance > 32768) throw new InvalidDataException("Invalid Puff match distance.");
            return (1, length, distance);
        }
    }
}