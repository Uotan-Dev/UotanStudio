using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.IO;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Writing;

internal static partial class DexWriter
{
    private static string NormalizeVersion(string version)
    {
        if (version is not ("035" or "037" or "038" or "039" or "040"))
        {
            throw new DexException($"Unsupported DEX version '{version}'; supported versions are 035, 037, 038, 039 and 040.");
        }
        foreach (var c in version)
        {
            if (c < '0' || c > '9') throw new DexException($"Invalid DEX version '{version}'");
        }
        return version;
    }

    private static void WriteStringData(BinaryWriter w, string value)
    {
        var mutf8 = Mutf8.Encode(value);
        WriteUleb128(w, (uint)value.Length);
        w.Write(mutf8);
        w.Write((byte)0);
    }

    private static void WriteTypeList(BinaryWriter w, IReadOnlyList<int> typeIndices)
    {
        w.Write((uint)typeIndices.Count);
        foreach (var t in typeIndices)
        {
            w.Write((ushort)t);
        }
    }

    /// <summary>
    /// Writes a code_item (including tries and the catch handler list); returns the code_item start position so the caller can patch debug_info_off.
    /// <para>写出 code_item（含异常保护块与处理器列表）；返回 code_item 起始位置，供回填 debug_info_off。</para>
    /// </summary>
    private static int WriteCodeItem(BinaryWriter w, CodeItem code)
    {
        var start = (int)w.BaseStream.Position;
        w.Write(code.RegistersSize);
        w.Write(code.InsSize);
        w.Write(code.OutsSize);
        w.Write((ushort)code.Tries.Count);
        w.Write(0u);
        w.Write((uint)code.RawInsns.Length);
        foreach (var unit in code.RawInsns)
        {
            w.Write(unit);
        }

        if (code.Tries.Count > 0)
        {

            Align(w, 4);

            var listHeaderSize = Leb128.Uleb128Size((uint)code.Handlers.Count);
            var handlerOffsets = new int[code.Handlers.Count];
            var running = listHeaderSize;
            for (var i = 0; i < code.Handlers.Count; i++)
            {
                handlerOffsets[i] = running;
                running += CatchHandlerSize(code.Handlers[i]);
            }

            foreach (var t in code.Tries)
            {
                w.Write((uint)t.StartAddress);
                w.Write(t.InsnCount);
                var off = t.HandlerIndex >= 0 && t.HandlerIndex < handlerOffsets.Length
                    ? handlerOffsets[t.HandlerIndex]
                    : 0;
                w.Write((ushort)off);
            }

            WriteUleb128(w, (uint)code.Handlers.Count);
            foreach (var handler in code.Handlers)
            {
                WriteCatchHandler(w, handler);
            }
        }
        return start;
    }

    /// <summary>
    /// Computes the encoded byte length of one encoded_catch_handler (consistent with WriteCatchHandler).
    /// <para>计算单个 encoded_catch_handler 的编码字节长度（与 WriteCatchHandler 一致）。</para>
    /// </summary>
    private static int CatchHandlerSize(CatchHandler handler)
    {
        var typedCount = handler.Entries.Count;
        var size = Leb128.Sleb128Size(handler.HasCatchAll ? -typedCount : typedCount);
        foreach (var entry in handler.Entries)
        {
            size += Leb128.Uleb128Size((uint)entry.TypeIndex);
            size += Leb128.Uleb128Size((uint)entry.Address);
        }
        if (handler.HasCatchAll)
        {
            size += Leb128.Uleb128Size((uint)handler.CatchAllAddress);
        }
        return size;
    }

    /// <summary>
    /// Writes a single encoded_catch_handler (sleb128 type count + type/address pairs + optional catch-all).
    /// <para>写出单个 encoded_catch_handler（sleb128 类型数 + 类型/地址对 + 可选 catch_all）。</para>
    /// </summary>
    private static void WriteCatchHandler(BinaryWriter w, CatchHandler handler)
    {
        var typedCount = handler.Entries.Count;

        WriteSleb128(w, handler.HasCatchAll ? -typedCount : typedCount);
        foreach (var entry in handler.Entries)
        {
            WriteUleb128(w, (uint)entry.TypeIndex);
            WriteUleb128(w, (uint)entry.Address);
        }
        if (handler.HasCatchAll)
        {
            WriteUleb128(w, (uint)handler.CatchAllAddress);
        }
    }

    /// <summary>
    /// Writes an encoded value (encoded_value), minimally encoded per value_arg/value_type.
    /// <para>写出编码值（encoded_value），按 value_arg/value_type 极小编码。</para>
    /// </summary>
    private static void WriteEncodedValue(BinaryWriter w, EncodedValue value, int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("Encoded value nesting exceeds 64.");
        }
        switch (value.Type)
        {
            case EncodedValueType.Byte:
                w.Write((byte)EncodedValueType.Byte);
                w.Write((byte)value.AsInt());
                break;
            case EncodedValueType.Short:
                {
                    var v = (short)value.AsInt();
                    var n = SignedByteCount(v, 2);
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Short));
                    for (var i = 0; i < n; i++) w.Write((byte)(v >> (i * 8)));
                    break;
                }
            case EncodedValueType.Char:
                {
                    var v = (char)value.AsInt();
                    var n = v <= 0xFF ? 1 : 2;
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Char));
                    for (var i = 0; i < n; i++) w.Write((byte)(v >> (i * 8)));
                    break;
                }
            case EncodedValueType.Int:
                {
                    var v = value.AsInt();
                    var n = SignedByteCount(v, 4);
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Int));
                    for (var i = 0; i < n; i++) w.Write((byte)(v >> (i * 8)));
                    break;
                }
            case EncodedValueType.Long:
                {
                    var v = value.AsLong();
                    var n = SignedByteCount(v, 8);
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Long));
                    for (var i = 0; i < n; i++) w.Write((byte)(v >> (i * 8)));
                    break;
                }
            case EncodedValueType.Float:
                {
                    var bits = BitConverter.SingleToUInt32Bits((float)(value.Value ?? 0f));
                    var n = 4;
                    while (n > 1 && (bits & 255) == 0)
                    {
                        bits >>= 8;
                        n--;
                    }
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Float));
                    for (var i = 0; i < n; i++) w.Write((byte)(bits >> (i * 8)));
                    break;
                }
            case EncodedValueType.Double:
                {
                    var bits = BitConverter.DoubleToUInt64Bits((double)(value.Value ?? 0d));
                    var n = 8;
                    while (n > 1 && (bits & 255) == 0)
                    {
                        bits >>= 8;
                        n--;
                    }
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Double));
                    for (var i = 0; i < n; i++) w.Write((byte)(bits >> (i * 8)));
                    break;
                }
            case EncodedValueType.MethodType:
            case EncodedValueType.MethodHandle:
            case EncodedValueType.String:
            case EncodedValueType.Type:
            case EncodedValueType.Field:
            case EncodedValueType.Method:
            case EncodedValueType.Enum:
                {
                    var idx = value.AsIndex();
                    var n = UnsignedByteCount((uint)idx, 4);
                    w.Write((byte)((n - 1) << 5 | (byte)value.Type));
                    for (var i = 0; i < n; i++) w.Write((byte)(idx >> (i * 8)));
                    break;
                }
            case EncodedValueType.Array:
                {
                    w.Write((byte)EncodedValueType.Array);
                    var items = value.AsArray() ?? [];
                    WriteUleb128(w, (uint)items.Length);
                    foreach (var item in items) WriteEncodedValue(w, item, depth + 1);
                    break;
                }
            case EncodedValueType.Annotation:
                {
                    w.Write((byte)EncodedValueType.Annotation);
                    var item = value.Value as AnnotationItem;
                    WriteEncodedAnnotation(w, item ?? new AnnotationItem(), depth + 1);
                    break;
                }
            case EncodedValueType.Null:
                w.Write((byte)EncodedValueType.Null);
                break;
            case EncodedValueType.Boolean:
                w.Write((byte)((value.AsBoolean() ? 1 : 0) << 5 | (byte)EncodedValueType.Boolean));
                break;
            default:
                throw new DexException($"Unsupported encoded_value type {value.Type}");
        }
    }

    /// <summary>
    /// Writes an encoded annotation (encoded_annotation).
    /// <para>写出编码注解（encoded_annotation）。</para>
    /// </summary>
    private static void WriteEncodedAnnotation(BinaryWriter w, AnnotationItem item, int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("Encoded annotation nesting exceeds 64.");
        }
        WriteUleb128(w, (uint)item.TypeIndex);
        WriteUleb128(w, (uint)item.Elements.Count);
        foreach (var element in item.Elements)
        {
            WriteUleb128(w, (uint)element.NameIndex);
            WriteEncodedValue(w, element.Value, depth + 1);
        }
    }

    /// <summary>
    /// Computes the minimum byte count (≤ maxBytes) to represent a signed value by sign extension.
    /// <para>计算有符号值以符号扩展表示所需的最小字节数（≤ maxBytes）。</para>
    /// </summary>
    private static int SignedByteCount(long v, int maxBytes)
    {
        for (var n = 1; n < maxBytes; n++)
        {
            var shift = n * 8 - 1;
            if (v >= -(1L << shift) && v <= (1L << shift) - 1) return n;
        }
        return maxBytes;
    }

    /// <summary>
    /// Computes the minimum byte count (≤ maxBytes) to represent an unsigned value by right zero extension.
    /// <para>计算无符号值右零扩展表示所需的最小字节数（≤ maxBytes）。</para>
    /// </summary>
    private static int UnsignedByteCount(ulong v, int maxBytes)
    {
        var n = maxBytes;
        while (n > 1 && (v >> (8 * (n - 1))) == 0) n--;
        return n;
    }

    private static void WriteDiff(BinaryWriter w, int index, ref int previous)
    {
        WriteUleb128(w, (uint)(index - previous));
        previous = index;
    }

    private static void WriteUleb128(BinaryWriter w, uint value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var end = Leb128.WriteUleb128(buffer, 0, value);
        w.Write(buffer[..end]);
    }

    private static void WriteSleb128(BinaryWriter w, int value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var end = Leb128.WriteSleb128(buffer, 0, value);
        w.Write(buffer[..end]);
    }

    private static void WriteOffsetPlaceholder(BinaryWriter w) => w.Write(0u);

    private static void Align(BinaryWriter w, int alignment)
    {
        var pad = (alignment - ((int)w.BaseStream.Position % alignment)) % alignment;
        for (var i = 0; i < pad; i++) w.Write((byte)0);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
        => SharedKernel.Checksums.Adler32.Compute(data);
}