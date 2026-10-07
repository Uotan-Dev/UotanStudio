using System.Buffers.Binary;

namespace UotanStudio.Firmware.AndroidMeta.Art;

internal static partial class VdexFile
{
    private static Task ConvertCompactAsync(Stream input, VdexEntry entry, Stream output, ArtData quick, CancellationToken token)
        => new CompactExport(input, entry, output, quick, token).RunAsync();

    private sealed class CompactExport(Stream input, VdexEntry entry, Stream output, ArtData quick, CancellationToken token)
    {
        private readonly ArtData _main = new(input, entry.Offset, entry.Size, token);
        private readonly ArtData _shared = new(input, entry.SharedOffset, entry.SharedSize, token);
        private readonly Dictionary<ushort, (uint Count, uint Offset)> _maps = [];
        private readonly Dictionary<uint, uint> _codes = [];
        private uint _dataBase;
        private int _budget = 1_000_000;
        private long _instructionBudget = 64 * 1024 * 1024;
        private static readonly ushort[] MandatoryMapTypes = [0, 0x1000];
        private static readonly ushort[] RelocatedMapTypes = [0x1002, 0x1003, 0x2006];
        private ArtData Target => new(output, 0, output.Length, token);

        internal async Task RunAsync()
        {
            Validate();
            input.Position = entry.Offset;
            await CopyAsync(input, output, entry.Size, token).ConfigureAwait(false);
            Align();
            _dataBase = checked((uint)output.Position);
            input.Position = entry.SharedOffset;
            await CopyAsync(input, output, entry.SharedSize, token).ConfigureAwait(false);
            output.Position = 0;
            output.Write("dex\n039\0"u8);
            Target.Write32(36, 112);
            RelocateFixedTables();
            RelocateDataPointers();
            await WriteCodeItemsAsync().ConfigureAwait(false);
            WriteClassData();
            WriteMap();
            var target = Target;
            target.Write32(32, checked((uint)output.Length));
            target.Write32(104, checked((uint)output.Length - _dataBase));
            target.Write32(108, _dataBase);
            var table = quick.Length == 0 ? null : new CompactOffsetTable(quick, entry.QuickTable, target.U32(88));
            long cursor = 0;
            Restore(target, quick, ref cursor, true, token, modernTable: table, modern: true);
            target.WriteDexHashes();
            target.VerifyDexHashes();
            await output.FlushAsync(token).ConfigureAwait(false);
        }

        private void Validate()
        {
            if (_main.U32(44) != 0 || _main.U32(48) != 0)
            {
                throw new NotSupportedException("CompactDex link data is not supported.");
            }
            foreach (var (offset, width) in new[] { (56, 4), (64, 4), (72, 12), (80, 8), (88, 8), (96, 32) })
            {
                var count = _main.U32(offset);
                var start = _main.U32(offset + 4);
                if (count > 1_000_000 || count != 0 && start < 136)
                {
                    throw new InvalidDataException("Invalid CompactDex identifier table.");
                }
                _main.Check(start, count * (long)width);
            }
            var map = _main.U32(52);
            var mapCount = _shared.U32(map);
            if (mapCount > 64)
            {
                throw new InvalidDataException("CompactDex map count exceeds the limit.");
            }
            _shared.Check(map + 4L, mapCount * 12L);
            for (var i = 0; i < mapCount; i++)
            {
                var at = map + 4L + i * 12L;
                var type = _shared.U16(at);
                var count = _shared.U32(at + 4);
                var offset = _shared.U32(at + 8);
                if (count > 1_000_000 || !_maps.TryAdd(type, (count, offset)))
                {
                    throw new InvalidDataException("Invalid or duplicate CompactDex map entry.");
                }
                if (type is > 8 and < 0x1000 or > 0x1003 and < 0x2000 or > 0x2006)
                {
                    throw new NotSupportedException($"Unsupported CompactDex map type 0x{type:x4}.");
                }
                (type < 0x1000 ? _main : _shared).Check(offset, count == 0 ? 0 : 1);
            }
            foreach (var type in MandatoryMapTypes)
            {
                if (!_maps.ContainsKey(type))
                {
                    throw new InvalidDataException("Missing mandatory CompactDex map entry.");
                }
            }
        }

        private uint Relocated(uint offset)
        {
            if (offset == 0)
            {
                return 0;
            }
            _shared.Check(offset, 1);
            return checked(_dataBase + offset);
        }

        private void PatchSharedPointer(long targetOffset, uint value) => Target.Write32(targetOffset, Relocated(value));

        private void RelocateFixedTables()
        {
            for (var i = 0; i < _main.U32(56); i++)
            {
                var at = _main.U32(60) + 4L * i;
                PatchSharedPointer(at, _main.U32(at));
            }
            for (var i = 0; i < _main.U32(72); i++)
            {
                var at = _main.U32(76) + 12L * i + 8;
                PatchSharedPointer(at, _main.U32(at));
            }
            for (var i = 0; i < _main.U32(96); i++)
            {
                var at = _main.U32(100) + 32L * i;
                foreach (var field in new[] { 12, 20, 28 })
                {
                    PatchSharedPointer(at + field, _main.U32(at + field));
                }
            }
            if (_maps.TryGetValue(7, out var callSites))
            {
                _main.Check(callSites.Offset, callSites.Count * 4L);
                for (var i = 0; i < callSites.Count; i++)
                {
                    var at = callSites.Offset + 4L * i;
                    PatchSharedPointer(at, _main.U32(at));
                }
            }
        }

        private void RelocateDataPointers()
        {
            foreach (var type in RelocatedMapTypes)
            {
                if (!_maps.TryGetValue(type, out var section))
                {
                    continue;
                }
                long cursor = section.Offset;
                for (var i = 0; i < section.Count; i++)
                {
                    cursor = (cursor + 3) & ~3L;
                    long count;
                    if (type == 0x2006)
                    {
                        PatchSharedPointer(_dataBase + cursor, _shared.U32(cursor));
                        count = (long)_shared.U32(cursor + 4) + _shared.U32(cursor + 8) + _shared.U32(cursor + 12);
                        cursor += 16;
                    }
                    else
                    {
                        count = _shared.U32(cursor);
                        cursor += 4;
                    }
                    if (count > _budget)
                    {
                        throw new InvalidDataException("CompactDex annotation tables exceed the work limit.");
                    }
                    _budget -= (int)count;
                    var width = type == 0x2006 ? 8 : 4;
                    _shared.Check(cursor, count * width);
                    for (var j = 0; j < count; j++, cursor += width)
                    {
                        var at = cursor + width - 4;
                        PatchSharedPointer(_dataBase + at, _shared.U32(at));
                    }
                }
            }
        }

        private IEnumerable<(int Class, uint Method, uint Code)> Methods()
        {
            for (var i = 0; i < _main.U32(96); i++)
            {
                long at = _main.U32(_main.U32(100) + i * 32L + 24);
                if (at == 0)
                {
                    continue;
                }
                var staticFields = _shared.Leb(ref at);
                var instanceFields = _shared.Leb(ref at);
                var direct = _shared.Leb(ref at);
                var virtuals = _shared.Leb(ref at);
                var count = (long)staticFields + instanceFields + direct + virtuals;
                if (count > _budget)
                {
                    throw new InvalidDataException("CompactDex class data exceeds the work limit.");
                }
                _budget -= (int)count;
                for (long f = 0; f < (long)staticFields + instanceFields; f++)
                {
                    _ = _shared.Leb(ref at);
                    _ = _shared.Leb(ref at);
                }
                foreach (var methods in new[] { direct, virtuals })
                {
                    long index = 0;
                    for (var m = 0; m < methods; m++)
                    {
                        index += _shared.Leb(ref at);
                        _ = _shared.Leb(ref at);
                        var code = _shared.Leb(ref at);
                        if (index >= _main.U32(88))
                        {
                            throw new InvalidDataException("Invalid CompactDex method index.");
                        }
                        if (code != 0)
                        {
                            yield return (i, (uint)index, code);
                        }
                    }
                }
            }
        }

        private async Task WriteCodeItemsAsync()
        {
            output.Position = output.Length;
            Align();
            var first = checked((uint)output.Position);
            var methodCount = _main.U32(88);
            var debug = new CompactOffsetTable(_shared, _main.U32(116), methodCount, _main.U32(124), _main.U32(120));
            foreach (var method in Methods())
            {
                Align();
                if (!_codes.TryAdd(method.Method, checked((uint)output.Position)))
                {
                    throw new InvalidDataException("Duplicate CompactDex method definition.");
                }
                var fields = _shared.U16(method.Code);
                var flags = _shared.U16(method.Code + 2L);
                long pre = method.Code;
                long units = flags >> 5;
                if ((flags & 16) != 0)
                {
                    units += _shared.U16(pre -= 2);
                    units += (long)_shared.U16(pre -= 2) << 16;
                }
                var values = new int[] { (fields >> 12) & 15, (fields >> 8) & 15, (fields >> 4) & 15, fields & 15 };
                for (var i = 0; i < 4; i++)
                {
                    if ((flags & (1 << i)) != 0)
                    {
                        values[i] += _shared.U16(pre -= 2);
                    }
                }
                values[0] += values[1];
                _instructionBudget -= units;
                if (_instructionBudget < 0 || values.IndexOfAnyExceptInRange(0, ushort.MaxValue) >= 0 || (method.Code & 1) != 0)
                {
                    throw new InvalidDataException("CompactDex code item exceeds its limits.");
                }
                _shared.Check(method.Code + 4L, units * 2);
                var header = new byte[16];
                for (var i = 0; i < 4; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(i * 2), (ushort)values[i]);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Relocated(debug.Get(method.Method)));
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)units);
                output.Write(header);
                input.Position = entry.SharedOffset + method.Code + 4;
                await CopyAsync(input, output, units * 2, token).ConfigureAwait(false);
                if (values[3] != 0)
                {
                    Align();
                    var tries = (method.Code + 4L + units * 2 + 3) & ~3L;
                    _shared.Check(tries, values[3] * 8L);
                    var handlers = tries + values[3] * 8L;
                    var end = handlers;
                    var count = _shared.Leb(ref end);
                    if (count > 65536)
                    {
                        throw new InvalidDataException("Too many CompactDex catch handlers.");
                    }
                    for (var h = 0; h < count; h++)
                    {
                        var size = _shared.SignedLeb(ref end);
                        var pairs = Math.Abs((long)size);
                        if (pairs > _budget)
                        {
                            throw new InvalidDataException("CompactDex catch data exceeds the work limit.");
                        }
                        _budget -= (int)pairs;
                        for (var p = 0; p < pairs; p++)
                        {
                            if (_shared.Leb(ref end) >= _main.U32(64) || _shared.Leb(ref end) >= units)
                            {
                                throw new InvalidDataException("Invalid CompactDex catch handler.");
                            }
                        }
                        if (size <= 0 && _shared.Leb(ref end) >= units)
                        {
                            throw new InvalidDataException("Invalid CompactDex catch-all handler.");
                        }
                    }
                    input.Position = entry.SharedOffset + tries;
                    await CopyAsync(input, output, end - tries, token).ConfigureAwait(false);
                }
            }
            _maps[0x2001] = ((uint)_codes.Count, first);
        }

        private void WriteClassData()
        {
            output.Position = output.Length;
            var first = checked((uint)output.Position);
            uint count = 0;
            for (var i = 0; i < _main.U32(96); i++)
            {
                var classAt = _main.U32(100) + i * 32L + 24;
                long source = _main.U32(classAt);
                if (source == 0)
                {
                    continue;
                }
                var offset = checked((uint)output.Position);
                Target.Write32(classAt, offset);
                output.Position = offset;
                var counts = new uint[4];
                for (var j = 0; j < 4; j++)
                {
                    counts[j] = _shared.Leb(ref source);
                    WriteLeb(counts[j]);
                }
                for (var group = 0; group < 4; group++)
                {
                    long index = 0;
                    for (var j = 0; j < counts[group]; j++)
                    {
                        var delta = _shared.Leb(ref source);
                        index += delta;
                        WriteLeb(delta);
                        WriteLeb(_shared.Leb(ref source));
                        if (group >= 2)
                        {
                            var oldCode = _shared.Leb(ref source);
                            WriteLeb(oldCode == 0 ? 0 : _codes[(uint)index]);
                        }
                    }
                }
                count++;
            }
            _maps[0x2000] = (count, first);
        }

        private void WriteMap()
        {
            output.Position = output.Length;
            Align();
            var start = checked((uint)output.Position);
            _maps[0x1000] = (1, start);
            var entries = _maps.Where(p => p.Value.Count != 0).Select(p =>
                (Type: p.Key, p.Value.Count, Offset: p.Key is 0x1000 or 0x2000 or 0x2001 || p.Key < 0x1000
                    ? p.Value.Offset : Relocated(p.Value.Offset))).OrderBy(p => p.Offset).ToArray();
            var bytes = new byte[4 + entries.Length * 12];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                var at = bytes.AsSpan(4 + i * 12);
                BinaryPrimitives.WriteUInt16LittleEndian(at, entries[i].Type);
                BinaryPrimitives.WriteUInt32LittleEndian(at[4..], entries[i].Count);
                BinaryPrimitives.WriteUInt32LittleEndian(at[8..], entries[i].Offset);
            }
            output.Write(bytes);
            Target.Write32(52, start);
        }

        private void WriteLeb(uint value)
        {
            token.ThrowIfCancellationRequested();
            do
            {
                var b = (byte)(value & 127);
                value >>= 7;
                output.WriteByte(value == 0 ? b : (byte)(b | 128));
            } while (value != 0);
        }

        private void Align()
        {
            token.ThrowIfCancellationRequested();
            while ((output.Position & 3) != 0)
            {
                output.WriteByte(0);
            }
            if (output.Position > uint.MaxValue)
            {
                throw new InvalidDataException("Converted DEX exceeds UInt32.");
            }
        }
    }
}