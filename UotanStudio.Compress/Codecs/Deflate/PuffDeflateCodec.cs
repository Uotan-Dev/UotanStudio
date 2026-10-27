using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Compress.Internal;

namespace UotanStudio.Compress.Codecs.Deflate;

/// <summary>
/// Converts between a DEFLATE bitstream and the puff instruction stream a delta patch carries so it can address bytes
/// inside a compressed payload without recompressing it.
/// <para>在 DEFLATE 位流与增量补丁所携带的 puff 指令流之间双向转换，使补丁能够定位压缩载荷内部的字节而无需重新压缩。</para>
/// </summary>
/// <remarks>
/// <see cref="PuffDeflateCodec.Puff"/> reads a bit extent of a DEFLATE stream and emits literal runs plus
/// back-reference tokens; <see cref="PuffDeflateCodec.Huff"/> replays those tokens to reproduce the original bytes.
/// Both directions keep the literal, length and distance bases, the stored-block handling and every boundary check
/// the puff format defines, because a stream puffed by one variant has to huff back with the same variant.
/// Output is bounded by the declared extent length through <see cref="BoundedWriteStream"/> before any byte
/// lands, so a stream that decodes past its declared size fails instead of growing without limit.
/// <para><see cref="PuffDeflateCodec.Puff"/> 读取 DEFLATE 位流的某个比特区间并输出字面量段与回指标记；
/// <see cref="PuffDeflateCodec.Huff"/> 按这些标记还原原始字节。
/// 两个方向都必须遵循 puff 格式定义的字面量、长度与距离基表、stored 块处理以及所有边界检查，
/// 因为按某一变体 puff 出来的流必须用同一变体 huff 回去。
/// 输出在落地前就由 <see cref="BoundedWriteStream"/> 按声明长度设界，因此解码超出既定大小的流会失败，而不是无限增长。</para>
/// </remarks>
public static class PuffDeflateCodec
{
    private static readonly int[] Order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    /// <summary>
    /// The largest literal run the puff writer stages before emitting it, fixed by the largest run the instruction
    /// stream can encode in one group.
    /// <para>puff 写出方在发出前暂存的最大字面量段，由指令流单组能编码的最大长度决定。</para>
    /// </summary>
    private const int MaximumLiteralRun = 65663;

    /// <summary>
    /// Initial capacity of the per-call metadata list, large enough for the three dynamic-header counts, the packed
    /// code-length nibbles and every code length, so one call never grows it again.
    /// <para>每次调用的元数据列表初始容量，足以容纳三个动态头部计数、打包的码长半字节以及全部码长，使单次调用不再扩容。</para>
    /// </summary>
    private const int MaximumMetadataEntries = 352;
    private static readonly int[] LengthBases = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBases = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    /// <summary>
    /// Expands a DEFLATE bit extent into its puff instruction stream.
    /// <para>把 DEFLATE 比特区间展开成 puff 指令流。</para>
    /// </summary>
    /// <param name="input">
    /// Buffer holding the packed DEFLATE data; it is read but never modified.
    /// <para>保存压缩位数据的缓冲区；只读不改。</para>
    /// </param>
    /// <param name="offset">Bit index of the first bit of the extent. <para>区间首个比特的比特索引。</para></param>
    /// <param name="length">Number of bits the extent covers. <para>区间覆盖的比特数。</para></param>
    /// <param name="expectedLength">Exact number of output bytes the extent must produce; it also caps the buffered
    /// output and must stay within 256 MiB. <para>区间必须产出的精确字节数，同时作为输出缓冲上限，且不得超过 256 MiB。</para></param>
    /// <returns>The puff instruction bytes produced from the extent. <para>由该区间产生的 puff 指令字节。</para></returns>
    /// <exception cref="InvalidDataException">The extent is reserved or truncated, a symbol or Huffman tree is invalid,
    /// or the decoded bit position and byte count disagree with the declared extent.
    /// <para>区间为保留类型或被截断、符号或 Huffman 树非法、或解码得到的比特位置与字节数和声明区间不符。</para></exception>
    /// <exception cref="IOException">The decoded output exceeds <paramref name="expectedLength"/>.
    /// <para>解码输出超出 <paramref name="expectedLength"/>。</para></exception>
    /// <exception cref="OverflowException"><paramref name="offset"/> plus <paramref name="length"/> overflows.
    /// <para><paramref name="offset"/> 与 <paramref name="length"/> 相加溢出。</para></exception>
    public static byte[] Puff(byte[] input, long offset, long length, int expectedLength)
    {
        var bits = new Bits(input, offset, checked(offset + length));
        if (expectedLength is < 0 or > 256 * 1024 * 1024) throw new InvalidDataException("Puff extent exceeds the buffer limit.");
        using var result = new MemoryStream(expectedLength);
        using var output = new BoundedWriteStream(result, expectedLength, leaveOpen: true);
        var literals = ArrayPool<byte>.Shared.Rent(MaximumLiteralRun);
        var literalCount = 0;
        var metadata = new List<byte>(MaximumMetadataEntries);
        try
        {
            void FlushLiterals()
            {
                if (literalCount == 0) return;
                if (literalCount <= 127) output.WriteByte((byte)(literalCount - 1));
                else { output.WriteByte(127); WriteBig16(output, literalCount - 128); }
                output.Write(literals.AsSpan(0, literalCount));
                literalCount = 0;
            }

            while (bits.Position < bits.End)
            {
                var final = bits.Read(1); var type = bits.Read(2);
                metadata.Clear();
                metadata.Add((byte)((final << 7) | (type << 5)));
                Huffman literalTree; Huffman distanceTree;
                if (type == 0)
                {
                    metadata[0] |= (byte)bits.Read((int)((8 - (bits.Position & 7)) & 7));
                    var count = bits.Read(16);
                    if ((count ^ bits.Read(16)) != 65535) throw new InvalidDataException("Invalid stored DEFLATE length.");
                    WriteBig16(output, 0); output.WriteByte(metadata[0]);
                    for (var i = 0; i < count; i++) literals[literalCount++] = (byte)bits.Read(8);
                    FlushLiterals(); output.Write([255, 129]);
                    continue;
                }
                if (type == 1) (literalTree, distanceTree) = FixedTrees();
                else if (type == 2) (literalTree, distanceTree) = ReadDynamic(bits, metadata);
                else throw new InvalidDataException("Reserved DEFLATE block type.");
                try
                {
                    WriteBig16(output, metadata.Count - 1); foreach (var value in metadata) output.WriteByte(value);
                    while (true)
                    {
                        var symbol = literalTree.Read(bits);
                        if (symbol < 256)
                        {
                            literals[literalCount++] = (byte)symbol; if (literalCount == MaximumLiteralRun) FlushLiterals();
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
                finally
                {
                    literalTree.ReleaseTable();
                    distanceTree.ReleaseTable();
                }
            }
            if (bits.Position != bits.End || output.BytesWritten != expectedLength) throw new InvalidDataException("Puff extent length mismatch.");
            return result.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(literals);
        }
    }

    /// <summary>
    /// Replays a puff instruction stream into the DEFLATE bytes it was produced from.
    /// <para>按 puff 指令流回放其对应的 DEFLATE 字节。</para>
    /// </summary>
    /// <param name="puff">Puff instruction tokens to replay. <para>待回放的 puff 指令标记。</para></param>
    /// <param name="output">Buffer receiving the packed DEFLATE bits; its existing bytes are overwritten in place.
    /// <para>接收压缩比特的缓冲区；原有字节被就地覆写。</para></param>
    /// <param name="offset">Bit index of the first bit to write. <para>待写入首个比特的比特索引。</para></param>
    /// <param name="length">Number of bits the extent covers; the tokens must fill it exactly.
    /// <para>区间覆盖的比特数；标记必须恰好填满该区间。</para></param>
    /// <exception cref="InvalidDataException">A token, stored block or Huffman tree is invalid, or the written bit
    /// position does not land on the end of the declared extent.
    /// <para>标记、stored 块或 Huffman 树非法，或写入的比特位置没有恰好落在声明区间的末尾。</para></exception>
    /// <exception cref="OverflowException"><paramref name="offset"/> plus <paramref name="length"/> overflows.
    /// <para><paramref name="offset"/> 与 <paramref name="length"/> 相加溢出。</para></exception>
    public static void Huff(ReadOnlySpan<byte> puff, byte[] output, long offset, long length)
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
        Span<byte> codes = stackalloc byte[19];
        for (var i = 0; i < codeCount; i++)
        {
            codes[Order[i]] = (byte)bits.Read(3);
            if ((i & 1) == 0) metadata.Add((byte)(codes[Order[i]] << 4)); else metadata[^1] |= codes[Order[i]];
        }
        var codeTree = new Huffman(codes);
        Span<byte> lengths = stackalloc byte[286 + 30];
        var lengthCount = 0;
        try
        {
            while (lengthCount < literalCount + distanceCount)
            {
                var code = codeTree.Read(bits);
                if (code < 16) { lengths[lengthCount++] = (byte)code; metadata.Add((byte)code); }
                else
                {
                    var extra = bits.Read(code == 16 ? 2 : code == 17 ? 3 : code == 18 ? 7 : throw new InvalidDataException("Invalid DEFLATE code-length symbol."));
                    var packed = (byte)((code == 16 ? 16 : code == 17 ? 20 : 28) + extra);
                    metadata.Add(packed); ExpandLength(lengths, ref lengthCount, packed);
                }
            }
        }
        finally
        {
            codeTree.ReleaseTable();
        }

        if (lengthCount != literalCount + distanceCount) throw new InvalidDataException("DEFLATE code lengths exceed declared counts.");
        var literalTree = new Huffman(lengths[..literalCount]);
        try
        {
            return (literalTree, new Huffman(lengths[literalCount..]));
        }
        catch
        {
            literalTree.ReleaseTable();
            throw;
        }
    }

    private static (Huffman, Huffman) WriteDynamic(Bits bits, ReadOnlySpan<byte> metadata)
    {
        var reader = new PuffReader(metadata);
        var counts = reader.Take(3);
        var literalCount = counts[0] + 257; var distanceCount = counts[1] + 1; var codeCount = counts[2] + 4;
        if (literalCount > 286 || distanceCount > 30 || codeCount > 19) throw new InvalidDataException("Invalid Puff Huffman counts.");
        bits.Write(5, counts[0]); bits.Write(5, counts[1]); bits.Write(4, counts[2]);
        var packed = reader.Take((codeCount + 1) / 2); Span<byte> codes = stackalloc byte[19];
        for (var i = 0; i < codeCount; i++)
        { codes[Order[i]] = (byte)(((i & 1) == 0 ? packed[i / 2] >> 4 : packed[i / 2]) & 15); if (codes[Order[i]] > 7) throw new InvalidDataException("Invalid Puff code length."); bits.Write(3, codes[Order[i]]); }
        var codeTree = new Huffman(codes, decoding: false, retainCodes: true); Span<byte> lengths = stackalloc byte[286 + 30];
        var lengthCount = 0;
        while (lengthCount < literalCount + distanceCount)
        {
            var value = reader.Take(1)[0];
            var code = value < 16 ? value : value < 20 ? 16 : value < 28 ? 17 : 18;
            codeTree.Write(bits, code);
            if (code < 16) lengths[lengthCount++] = value;
            else { bits.Write(code == 16 ? 2 : code == 17 ? 3 : 7, value - (code == 16 ? 16 : code == 17 ? 20 : 28)); ExpandLength(lengths, ref lengthCount, value); }
        }
        if (lengthCount != literalCount + distanceCount || reader.Remaining != 0) throw new InvalidDataException("Invalid Puff Huffman metadata length.");
        return (new Huffman(lengths[..literalCount], decoding: false, retainCodes: true), new Huffman(lengths[literalCount..], decoding: false, retainCodes: true));
    }

    private static void ExpandLength(Span<byte> lengths, ref int lengthCount, byte packed)
    {
        if (packed > 155 || (packed < 20 && lengthCount == 0)) throw new InvalidDataException("Invalid Puff code repeat.");
        var count = packed < 20 ? packed - 16 + 3 : packed < 28 ? packed - 20 + 3 : packed - 28 + 11;
        var value = packed < 20 ? lengths[lengthCount - 1] : (byte)0;
        for (var i = 0; i < count; i++) lengths[lengthCount++] = value;
    }
    private static int FindBase(int[] values, int value)
    { var i = 0; while (i + 1 < values.Length && values[i + 1] <= value) i++; return i; }
    private static void WriteBig16(Stream output, int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value)); output.Write(bytes); }
    private static (Huffman, Huffman) FixedTrees()
        => (FixedLiteralTree, FixedDistanceTree);

    /// <summary>
    /// The RFC 1951 fixed literal/length code, built once. It is a compile-time constant of the format, and
    /// <see cref="Huffman"/> is immutable after construction, so every fixed-Huffman block reuses this instance
    /// instead of rebuilding 288 lengths, a code array and a single-level lookup table per block.
    /// <para>RFC 1951 的固定字面量/长度码，只构建一次。它是格式的编译期常量，且 <see cref="Huffman"/> 构造后不可变，
    /// 因此每个固定 Huffman 块都复用该实例，不必逐块重建 288 项长度、码数组与单级查找表。</para>
    /// </summary>
    private static readonly Huffman FixedLiteralTree = BuildFixedLiteralTree();

    /// <summary>
    /// The RFC 1951 fixed distance code, built once for the same reason as <see cref="FixedLiteralTree"/>.
    /// <para>RFC 1951 的固定距离码，与 <see cref="FixedLiteralTree"/> 同理只构建一次。</para>
    /// </summary>
    private static readonly Huffman FixedDistanceTree = BuildFixedDistanceTree();

    private static Huffman BuildFixedLiteralTree()
    {
        var literals = new byte[288];
        for (var i = 0; i < literals.Length; i++)
        {
            literals[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        }

        return new Huffman(literals, decoding: true, rentedTable: false, retainCodes: true);
    }

    private static Huffman BuildFixedDistanceTree()
    {
        var distances = new byte[32];
        Array.Fill(distances, (byte)5);
        return new Huffman(distances, decoding: true, rentedTable: false, retainCodes: true);
    }

    private sealed class Huffman
    {
        private readonly byte[]? _lengths;
        private readonly int[]? _codes;
        private ushort[]? _table;
        private int _tableBits;
        private bool _rentedTable;

        /// <summary>
        /// Builds the canonical code over the given lengths. Decoding trees only materialize the pooled lookup table —
        /// the code and length arrays stay on the stack — so per-block dynamic trees allocate nothing beyond the
        /// instance; trees used through <see cref="Write"/> pass <paramref name="retainCodes"/> so the arrays live as
        /// long as the tree does.
        /// <para>按给定码长构建规范码。解码树只物化池化查找表——码字与码长数组都留在栈上——因此逐块的动态树除实例本身
        /// 外零分配；需要经 <see cref="Write"/> 使用的树传入 <paramref name="retainCodes"/>，让数组与树同生命周期。</para>
        /// </summary>
        internal Huffman(ReadOnlySpan<byte> lengths, bool decoding = true, bool rentedTable = true, bool retainCodes = false)
        {
            Span<int> codes = stackalloc int[lengths.Length];
            Span<int> counts = stackalloc int[16];
            Span<int> next = stackalloc int[16];
            foreach (var length in lengths) { if (length > 15) throw new InvalidDataException("Invalid Huffman length."); counts[length]++; }
            counts[0] = 0; var code = 0;
            for (var length = 1; length <= 15; length++)
            { code = (code + counts[length - 1]) << 1; next[length] = code; if (code + counts[length] > 1 << length) throw new InvalidDataException("Oversubscribed Huffman tree."); }
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol]; if (length == 0) continue;
                var canonical = next[length]++; var reversed = 0;
                for (var bit = 0; bit < length; bit++) { reversed = (reversed << 1) | (canonical & 1); canonical >>= 1; }
                codes[symbol] = reversed;
            }

            if (retainCodes)
            {
                _lengths = lengths.ToArray();
                _codes = [.. codes];
            }

            if (decoding)
            {
                BuildTable(lengths, codes, rentedTable);
            }
        }

        /// <summary>
        /// Builds the single-level lookup table indexed by the next table-width bits, with each entry packing the symbol and its code length.
        /// <para>构建按紧随其后的表宽比特索引的单级查找表，每项打包符号与其码长。</para>
        /// </summary>
        private void BuildTable(ReadOnlySpan<byte> lengths, ReadOnlySpan<int> codes, bool rentedTable)
        {
            var bits = 0;
            foreach (var length in lengths) { if (length > bits) bits = length; }
            _tableBits = bits;
            if (bits == 0)
            {
                return;
            }

            var size = 1 << bits;
            var table = rentedTable ? ArrayPool<ushort>.Shared.Rent(size) : new ushort[size];
            if (rentedTable)
            {
                Array.Clear(table, 0, size);
            }

            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                var entry = (ushort)((symbol << 4) | length);
                for (var index = codes[symbol]; index < size; index += 1 << length)
                {
                    table[index] = entry;
                }
            }

            _table = table;
            _rentedTable = rentedTable;
        }

        internal int Read(Bits bits)
        {
            var table = _table;
            if (table == null)
            {
                if (bits.End - bits.Position < 15)
                {
                    throw new InvalidDataException("Truncated DEFLATE bits.");
                }

                throw new InvalidDataException("Invalid Huffman symbol.");
            }

            var available = (int)Math.Min(_tableBits, bits.End - bits.Position);
            if (available <= 0)
            {
                throw new InvalidDataException("Truncated DEFLATE bits.");
            }

            var entry = table[bits.Peek(available)];
            var length = entry & 0xF;
            if (length == 0)
            {
                if (bits.End - bits.Position < 15)
                {
                    throw new InvalidDataException("Truncated DEFLATE bits.");
                }

                throw new InvalidDataException("Invalid Huffman symbol.");
            }

            if (length > available)
            {
                throw new InvalidDataException("Truncated DEFLATE bits.");
            }

            bits.Advance(length);
            return entry >> 4;
        }

        /// <summary>
        /// Returns the pooled lookup table to the shared pool; tables built over a private array are kept.
        /// <para>把池化查找表归还共享池；基于私有数组构建的表保留。</para>
        /// </summary>
        internal void ReleaseTable()
        {
            if (!_rentedTable || _table == null)
            {
                return;
            }

            var table = _table;
            _table = null;
            _rentedTable = false;
            ArrayPool<ushort>.Shared.Return(table);
        }

        internal void Write(Bits bits, int symbol)
        {
            if (_lengths is null || _codes is null) throw new InvalidOperationException("This Huffman tree was built for decoding only.");
            if (symbol < 0 || symbol >= _lengths.Length || _lengths[symbol] == 0) throw new InvalidDataException("Unavailable Huffman symbol.");
            bits.Write(_lengths[symbol], _codes[symbol]);
        }
    }
    private sealed class Bits(byte[] data, long offset, long end)
    {
        private long _position = offset;
        private ulong _buffer;
        private int _bufferedBits;

        internal long Position => _position;

        internal long End => end;

        internal int Read(int count)
        {
            if (count < 0 || count > 16 || _position < 0 || end > (long)data.Length * 8 || count > end - _position) throw new InvalidDataException("Truncated DEFLATE bits.");
            if (count == 0)
            {
                return 0;
            }

            EnsureBuffered(count);
            var result = (int)(_buffer & ((1UL << count) - 1));
            _buffer >>= count;
            _bufferedBits -= count;
            _position += count;
            return result;
        }

        internal int Peek(int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            EnsureBuffered(count);
            return (int)(_buffer & ((1UL << count) - 1));
        }

        internal void Advance(int count)
        {
            _buffer >>= count;
            _bufferedBits -= count;
            _position += count;
        }

        private void EnsureBuffered(int count)
        {
            while (_bufferedBits < count)
            {
                var next = _position + _bufferedBits;
                var byteIndex = next >> 3;
                if (byteIndex < 0 || byteIndex >= data.Length)
                {
                    throw new InvalidDataException("Truncated DEFLATE bits.");
                }

                var bitOffset = (int)(next & 7);
                _buffer |= (ulong)(data[byteIndex] >> bitOffset) << _bufferedBits;
                _bufferedBits += 8 - bitOffset;
            }
        }

        internal void Write(int count, int value)
        {
            if (count < 0 || count > 16 || _position < 0 || end > (long)data.Length * 8 || count > end - _position) throw new InvalidDataException("Huffed DEFLATE exceeds its extent.");
            if (count == 0)
            {
                return;
            }

            for (var written = 0; written < count;)
            {
                var bitOffset = (int)(_position & 7);
                var byteIndex = (int)(_position >> 3);
                var take = Math.Min(count - written, 8 - bitOffset);
                var field = (1 << take) - 1;
                var mask = (byte)(field << bitOffset);
                var bits = (byte)((value >> written) & field);
                data[byteIndex] = (byte)((data[byteIndex] & ~mask) | (bits << bitOffset));
                _position += take;
                written += take;
            }
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