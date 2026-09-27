using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Art;

public static partial class VdexFile
{
    private static void Restore(ArtData dex, ArtData quick, ref long cursor, bool hasQuickening, CancellationToken token,
        Dictionary<uint, (long Start, long End)>? records010 = null, CompactOffsetTable? modernTable = null, bool modern = false)
    {
        dex.ValidateDex(modern);
        var classCount = dex.U32(96);
        var classes = dex.U32(100);
        var visited = new HashSet<uint>();
        var budget = 1_000_000;
        long instructionBudget = 64 * 1024 * 1024;
        for (var i = 0; i < classCount; i++)
        {
            token.ThrowIfCancellationRequested();
            long at = dex.U32(classes + i * 32L + 24);
            if (at == 0)
            {
                continue;
            }
            var staticFields = dex.Leb(ref at);
            var instanceFields = dex.Leb(ref at);
            var directMethods = dex.Leb(ref at);
            var virtualMethods = dex.Leb(ref at);
            var total = (long)staticFields + instanceFields + directMethods + virtualMethods;
            if (total > budget)
            {
                throw new InvalidDataException("ART class data exceeds the work limit.");
            }
            budget -= (int)total;
            foreach (var fieldCount in new[] { staticFields, instanceFields })
            {
                long index = 0;
                for (var field = 0; field < fieldCount; field++)
                {
                    index += dex.Leb(ref at);
                    _ = dex.Leb(ref at);
                    if (index >= dex.U32(80))
                    {
                        throw new InvalidDataException("Invalid encoded field index.");
                    }
                }
            }
            foreach (var methodCount in new[] { directMethods, virtualMethods })
            {
                long index = 0;
                for (var method = 0; method < methodCount; method++)
                {
                    index += dex.Leb(ref at);
                    _ = dex.Leb(ref at);
                    var code = dex.Leb(ref at);
                    if (index >= dex.U32(88))
                    {
                        throw new InvalidDataException("Invalid encoded method index.");
                    }
                    if (code == 0)
                    {
                        continue;
                    }
                    if ((code & 3) != 0 || code < dex.U32(108) || !visited.Add(code))
                    {
                        throw new InvalidDataException("Unaligned, overlapping or shared ART code item is unsupported.");
                    }
                    dex.Check(code, 16);
                    instructionBudget -= dex.U32(code + 12);
                    if (instructionBudget < 0)
                    {
                        throw new InvalidDataException("ART code items exceed the instruction work limit.");
                    }
                    long end = cursor;
                    if (modern)
                    {
                        var bounds = ModernQuickBounds(quick, modernTable?.Get((uint)index) ?? 0);
                        cursor = bounds.Start;
                        end = bounds.End;
                    }
                    else if (records010 is not null)
                    {
                        var bounds = records010.Remove(code, out var record) ? record : (0L, 0L);
                        cursor = bounds.Item1;
                        end = bounds.Item2;
                    }
                    else if (hasQuickening)
                    {
                        var size = quick.U32(cursor);
                        cursor += 4;
                        quick.Check(cursor, size);
                        end = cursor + size;
                    }
                    RestoreCode(dex, code, quick, ref cursor, end, hasQuickening, token, records010 is not null || modern);
                    if (cursor != end)
                    {
                        throw new InvalidDataException("Unused method quickening data.");
                    }
                }
            }
        }
        if (records010 is { Count: > 0 })
        {
            throw new InvalidDataException("VDEX 010 contains quickening records for absent code items.");
        }
    }

    private static void RestoreCode(ArtData dex, uint code, ArtData quick, ref long cursor, long end,
        bool hasQuickening, CancellationToken token, bool fixedIndices)
    {
        dex.Check(code, 16);
        var units = dex.U32(code + 12);
        long start = code + 16L;
        dex.Check(start, units * 2L);
        var hadIndices = cursor < end;
        for (long pc = 0; pc < units;)
        {
            token.ThrowIfCancellationRequested();
            var at = start + pc * 2;
            var word = dex.U16(at);
            var opcode = (byte)word;
            long size;
            if (word is 0x100 or 0x200 or 0x300)
            {
                if (fixedIndices && hadIndices && QuickIndex(quick, ref cursor, end, pc, true) != ushort.MaxValue)
                {
                    throw new InvalidDataException("DEX payload has non-sentinel quickening metadata.");
                }
                if ((pc & 1) != 0 || units - pc < (word == 0x200 ? 2 : 4))
                {
                    throw new InvalidDataException("Truncated or unaligned DEX payload.");
                }
                var n = dex.U16(at + 2);
                if (word == 0x300 && n is not (1 or 2 or 4 or 8))
                {
                    throw new InvalidDataException("Invalid array payload element width.");
                }
                size = word switch
                {
                    0x100 => 4 + 2L * n,
                    0x200 => 2 + 4L * n,
                    _ => 4 + ((long)n * dex.U32(at + 4) + 1) / 2
                };
            }
            else if (opcode is >= 0xe3 and <= 0xf2)
            {
                if (!hasQuickening)
                {
                    throw new InvalidDataException("Quickened opcode has no quickening metadata.");
                }
                size = opcode is 0xe9 or 0xea ? 3 : 2;
                CheckInstruction(pc, size, units);
                var original = opcode switch
                {
                    0xe3 => 0x52,
                    0xe4 => 0x53,
                    0xe5 => 0x54,
                    0xe6 => 0x59,
                    0xe7 => 0x5a,
                    0xe8 => 0x5b,
                    0xe9 => 0x6e,
                    0xea => 0x74,
                    0xeb => 0x5c,
                    0xec => 0x5d,
                    0xed => 0x5e,
                    0xee => 0x5f,
                    0xef => 0x55,
                    0xf0 => 0x56,
                    0xf1 => 0x57,
                    _ => 0x58
                };
                var index = QuickIndex(quick, ref cursor, end, pc, fixedIndices);
                if (index >= dex.U32(opcode is 0xe9 or 0xea ? 88 : 80))
                {
                    throw new InvalidDataException("Restored quickening index exceeds the DEX table.");
                }
                dex.Write16(at, (ushort)((word & 0xff00) | original));
                dex.Write16(at + 2, index);
            }
            else if (word == 0 && (fixedIndices ? hadIndices : cursor < end && PeekPc(quick, cursor, end) == pc))
            {
                var register = QuickIndex(quick, ref cursor, end, pc, fixedIndices);
                if (fixedIndices && register == ushort.MaxValue)
                {
                    pc++;
                    continue;
                }
                size = 2;
                CheckInstruction(pc, size, units);
                var type = QuickIndex(quick, ref cursor, end, pc, fixedIndices);
                if (register > 255 || register >= dex.U16(code) || type >= dex.U32(64) || dex.U16(at + 2) != 0)
                {
                    throw new InvalidDataException("Invalid eliminated check-cast metadata.");
                }
                dex.Write16(at, (ushort)(0x1f | (register << 8)));
                dex.Write16(at + 2, type);
            }
            else if (opcode == 0x73)
            {
                if (!hasQuickening || word != 0x73)
                {
                    throw new InvalidDataException("Return-void-no-barrier has no valid quickening context.");
                }
                size = 1;
                dex.Write16(at, 0x0e);
            }
            else
            {
                if (opcode is >= 0xf3 and <= 0xf9 or >= 0xfe ||
                    opcode is >= 0x3e and <= 0x43 or 0x79 or 0x7a ||
                    (opcode == 0 && word != 0) || OpCodes.Get(opcode) is null)
                {
                    throw new InvalidDataException("Unsupported ART instruction.");
                }
                size = OpCodes.GetSize(opcode);
            }
            CheckInstruction(pc, size, units);
            pc += size;
        }
    }

    private static void CheckInstruction(long pc, long size, long units)
    {
        if (size <= 0 || size > units - pc)
        {
            throw new InvalidDataException("Instruction exceeds its code item.");
        }
    }

    private static uint QuickLeb(ArtData quick, ref long cursor, long end)
    {
        if (cursor >= end)
        {
            throw new InvalidDataException("Missing quickening value.");
        }
        var value = quick.Leb(ref cursor);
        if (cursor > end)
        {
            throw new InvalidDataException("Quickening value exceeds method boundary.");
        }
        return value;
    }

    private static uint PeekPc(ArtData quick, long cursor, long end) => QuickLeb(quick, ref cursor, end);

    private static ushort QuickIndex(ArtData quick, ref long cursor, long end, long pc, bool fixedIndices)
    {
        if (fixedIndices)
        {
            if (cursor > end - 2)
            {
                throw new InvalidDataException("Missing VDEX 010 quickening index.");
            }
            var value = quick.U16(cursor);
            cursor += 2;
            return value;
        }
        if (QuickLeb(quick, ref cursor, end) != pc)
        {
            throw new InvalidDataException("Quickening PC does not match the instruction.");
        }
        var index = QuickLeb(quick, ref cursor, end);
        if (index > ushort.MaxValue)
        {
            throw new InvalidDataException("Quickening index exceeds UInt16.");
        }
        return (ushort)index;
    }
}