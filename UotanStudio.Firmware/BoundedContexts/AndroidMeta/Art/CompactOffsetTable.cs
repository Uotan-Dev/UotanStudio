namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Art;

/// <summary>Reads ART compact offset tables with modular UInt32 deltas.
/// <para>读取 ART 紧凑偏移表，其中差值采用 UInt32 模运算。</para></summary>
internal sealed class CompactOffsetTable
{
    private readonly ArtData _data;
    private readonly long _start;
    private readonly long _table;
    private readonly uint _minimum;
    private readonly uint _count;

    internal CompactOffsetTable(ArtData data, long start, uint count, uint? minimum = null, uint? tableOffset = null)
    {
        _data = data;
        _count = count;
        _minimum = minimum ?? data.U32(start);
        var relativeTable = tableOffset ?? data.U32(start + 4);
        _start = checked(start + (minimum.HasValue ? 0 : 8));
        _table = checked(_start + relativeTable);
        if (count > 1_000_000)
        {
            throw new InvalidDataException("Compact offset count exceeds the resource limit.");
        }
        var indexBytes = checked(((count + 15L) / 16) * 4);
        data.Check(_table, indexBytes);
        if (_table < _start || _table > data.Length - indexBytes)
        {
            throw new InvalidDataException("Compact offset index table is outside its owning section.");
        }
    }

    internal uint Get(uint index)
    {
        if (index >= _count)
        {
            throw new InvalidDataException("Compact offset index is outside its table.");
        }
        var indexOffset = checked(_table + index / 16L * 4);
        long cursor = checked(_start + _data.U32(indexOffset));
        if (cursor < _start || cursor > _table - 2)
        {
            throw new InvalidDataException("Compact offset block overlaps its index table.");
        }
        var mask = (_data.Byte(cursor) << 8) | _data.Byte(cursor + 1);
        cursor += 2;
        var bit = (int)(index % 16);
        if ((mask & (1 << bit)) == 0)
        {
            return 0;
        }
        var value = _minimum;
        for (var i = 0; i <= bit; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                value = unchecked(value + _data.Leb(ref cursor));
                if (cursor > _table)
                {
                    throw new InvalidDataException("Compact offset delta exceeds its block area.");
                }
            }
        }
        return value;
    }
}
