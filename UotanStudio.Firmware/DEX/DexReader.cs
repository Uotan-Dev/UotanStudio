using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex;

internal static class DexReader
{
    private const uint LittleEndianTag = 0x12345678;
    private const uint NoIndex = 0xFFFFFFFF;

    /// <summary>
    /// Parses a DEX byte array into the object model.
    /// <para>解析 DEX 字节数组为对象模型。</para>
    /// </summary>
    public static DexFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 0x70) throw new DexException("File too small to be a DEX (header is 0x70 bytes)");

        var magic = data.AsSpan(0, 8);
        if (!IsValidMagic(magic))
        {
            throw new DexException("Invalid DEX magic (expected 'dex\\n0xx\\0')");
        }

        var version = Encoding.ASCII.GetString(data, 4, 3);
        var endianTag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
        if (endianTag != LittleEndianTag)
        {
            throw new DexException($"Unsupported endian tag 0x{endianTag:X8} (only little-endian DEX is supported)");
        }

        var header = ReadHeader(data);
        var dex = new DexFile(header);

        ReadStringIds(data, dex);
        ReadTypeIds(data, dex);
        ReadProtoIds(data, dex);
        ReadFieldIds(data, dex);
        ReadMethodIds(data, dex);
        ReadClassDefs(data, dex);

        return dex;
    }

    private static bool IsValidMagic(ReadOnlySpan<byte> magic)
        => magic.Length == 8
           && magic[0] == (byte)'d' && magic[1] == (byte)'e' && magic[2] == (byte)'x'
           && magic[3] == (byte)'\n'
           && magic[4] >= (byte)'0' && magic[4] <= (byte)'9'
           && magic[5] >= (byte)'0' && magic[5] <= (byte)'9'
           && magic[6] >= (byte)'0' && magic[6] <= (byte)'9'
           && magic[7] == 0;

    private static DexHeader ReadHeader(byte[] data)
    {
        var span = data.AsSpan();
        return new DexHeader
        {
            Magic = span[..8].ToArray(),
            Version = Encoding.ASCII.GetString(data, 4, 3),
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
            Signature = span[12..32].ToArray(),
            FileSize = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]),
            HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]),
            EndianTag = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]),
            LinkSize = BinaryPrimitives.ReadUInt32LittleEndian(span[44..]),
            LinkOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[48..]),
            MapOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[52..]),
            StringIdsSize = BinaryPrimitives.ReadUInt32LittleEndian(span[56..]),
            StringIdsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[60..]),
            TypeIdsSize = BinaryPrimitives.ReadUInt32LittleEndian(span[64..]),
            TypeIdsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[68..]),
            ProtoIdsSize = BinaryPrimitives.ReadUInt32LittleEndian(span[72..]),
            ProtoIdsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[76..]),
            FieldIdsSize = BinaryPrimitives.ReadUInt32LittleEndian(span[80..]),
            FieldIdsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[84..]),
            MethodIdsSize = BinaryPrimitives.ReadUInt32LittleEndian(span[88..]),
            MethodIdsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[92..]),
            ClassDefsSize = BinaryPrimitives.ReadUInt32LittleEndian(span[96..]),
            ClassDefsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[100..]),
            DataSize = BinaryPrimitives.ReadUInt32LittleEndian(span[104..]),
            DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[108..])
        };
    }

    private static void ReadStringIds(byte[] data, DexFile dex)
    {
        var count = dex.Header.StringIdsSize;
        var offset = (int)dex.Header.StringIdsOffset;
        for (var i = 0; i < count; i++)
        {
            var itemOff = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4)));
            EnsureRange(data, itemOff, 1, "string_data_item");
            var p = itemOff;
            var utf16Size = Leb128.ReadUleb128(data, ref p);
            var start = p;
            while (p < data.Length && data[p] != 0) p++;
            if (p >= data.Length) throw new DexException($"String data {i} is not NUL-terminated");
            dex.Strings.Add(Mutf8.Decode(data, start, p - start));
            _ = utf16Size;
        }
    }

    private static void ReadTypeIds(byte[] data, DexFile dex)
    {
        var count = dex.Header.TypeIdsSize;
        var offset = (int)dex.Header.TypeIdsOffset;
        for (var i = 0; i < count; i++)
        {
            var descriptorIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4));
            dex.Types.Add(dex.GetString((int)descriptorIdx));
        }
    }

    private static void ReadProtoIds(byte[] data, DexFile dex)
    {
        var count = dex.Header.ProtoIdsSize;
        var offset = (int)dex.Header.ProtoIdsOffset;
        for (var i = 0; i < count; i++)
        {
            var baseOffset = offset + i * 12;
            var shortyIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset));
            var returnTypeIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 4));
            var parametersOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 8));
            var proto = new ProtoDefinition
            {
                ShortyIndex = (int)shortyIdx,
                ReturnTypeIndex = (int)returnTypeIdx
            };
            if (parametersOff != 0)
            {
                proto.ParameterTypeIndices = ReadTypeList(data, (int)parametersOff);
            }
            dex.Protos.Add(proto);
        }
    }

    private static int[] ReadTypeList(byte[] data, int offset)
    {
        EnsureRange(data, offset, 4, "type_list");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        var result = new int[size];
        var listSpan = data.AsSpan(offset + 4, checked((int)size * 2));
        if (BitConverter.IsLittleEndian)
        {


            var units = MemoryMarshal.Cast<byte, ushort>(listSpan);
            for (var i = 0; i < size; i++) result[i] = units[i];
        }
        else
        {
            for (var i = 0; i < size; i++)
            {
                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(listSpan[(i * 2)..]);
            }
        }
        return result;
    }

    private static void ReadFieldIds(byte[] data, DexFile dex)
    {
        var count = dex.Header.FieldIdsSize;
        var offset = (int)dex.Header.FieldIdsOffset;
        for (var i = 0; i < count; i++)
        {
            var baseOffset = offset + i * 8;
            dex.Fields.Add(new FieldId
            {
                ClassIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset)),
                TypeIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset + 2)),
                NameIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 4)))
            });
        }
    }

    private static void ReadMethodIds(byte[] data, DexFile dex)
    {
        var count = dex.Header.MethodIdsSize;
        var offset = (int)dex.Header.MethodIdsOffset;
        for (var i = 0; i < count; i++)
        {
            var baseOffset = offset + i * 8;
            dex.Methods.Add(new MethodId
            {
                ClassIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset)),
                ProtoIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset + 2)),
                NameIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 4)))
            });
        }
    }

    private static void ReadClassDefs(byte[] data, DexFile dex)
    {
        var count = dex.Header.ClassDefsSize;
        var offset = (int)dex.Header.ClassDefsOffset;
        for (var i = 0; i < count; i++)
        {
            var baseOffset = offset + i * 32;
            var cls = new ClassDefinition
            {
                TypeIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset))),
                AccessFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 4)),
                SuperclassIndex = ReadIndex(data, baseOffset + 8),
                SourceFileIndex = ReadIndex(data, baseOffset + 16)
            };
            var interfacesOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 12));
            if (interfacesOff != 0) cls.InterfaceTypeIndices = ReadTypeList(data, (int)interfacesOff);

            var classDataOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 24));
            if (classDataOff != 0) ReadClassData(data, (int)classDataOff, cls, dex);

            var annotationsOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 20));
            if (annotationsOff != 0)
            {
                cls.Annotations = ReadAnnotationsDirectory(data, (int)annotationsOff, dex);
            }

            var staticValuesOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 28));
            if (staticValuesOff != 0)
            {
                var p = (int)staticValuesOff;
                var size = Leb128.ReadUleb128(data, ref p);
                var values = new List<EncodedValue>(checked((int)size));
                for (var j = 0; j < size; j++)
                {
                    values.Add(ReadEncodedValue(data, ref p, dex));
                }
                cls.StaticValues = values;
            }

            dex.Classes.Add(cls);
        }
    }

    private static int ReadIndex(byte[] data, int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        return value == NoIndex ? -1 : checked((int)value);
    }

    private static void ReadClassData(byte[] data, int offset, ClassDefinition cls, DexFile dex)
    {
        var p = offset;
        var staticFieldsSize = Leb128.ReadUleb128(data, ref p);
        var instanceFieldsSize = Leb128.ReadUleb128(data, ref p);
        var directMethodsSize = Leb128.ReadUleb128(data, ref p);
        var virtualMethodsSize = Leb128.ReadUleb128(data, ref p);

        var fieldIdx = 0u;
        for (var i = 0; i < staticFieldsSize; i++)
        {
            fieldIdx += Leb128.ReadUleb128(data, ref p);
            var accessFlags = Leb128.ReadUleb128(data, ref p);
            cls.StaticFields.Add(new EncodedField { FieldIndex = (int)fieldIdx, AccessFlags = accessFlags });
        }
        fieldIdx = 0;
        for (var i = 0; i < instanceFieldsSize; i++)
        {
            fieldIdx += Leb128.ReadUleb128(data, ref p);
            var accessFlags = Leb128.ReadUleb128(data, ref p);
            cls.InstanceFields.Add(new EncodedField { FieldIndex = (int)fieldIdx, AccessFlags = accessFlags });
        }

        var methodIdx = 0u;
        for (var i = 0; i < directMethodsSize; i++)
        {
            methodIdx += Leb128.ReadUleb128(data, ref p);
            var accessFlags = Leb128.ReadUleb128(data, ref p);
            var codeOff = Leb128.ReadUleb128(data, ref p);
            cls.DirectMethods.Add(new EncodedMethod
            {
                MethodIndex = (int)methodIdx,
                AccessFlags = accessFlags,
                Code = codeOff != 0 ? ReadCodeItem(data, (int)codeOff, dex) : null
            });
        }
        methodIdx = 0;
        for (var i = 0; i < virtualMethodsSize; i++)
        {
            methodIdx += Leb128.ReadUleb128(data, ref p);
            var accessFlags = Leb128.ReadUleb128(data, ref p);
            var codeOff = Leb128.ReadUleb128(data, ref p);
            cls.VirtualMethods.Add(new EncodedMethod
            {
                MethodIndex = (int)methodIdx,
                AccessFlags = accessFlags,
                Code = codeOff != 0 ? ReadCodeItem(data, (int)codeOff, dex) : null
            });
        }
    }

    private static CodeItem ReadCodeItem(byte[] data, int offset, DexFile dex)
    {
        var baseOffset = offset;
        var registersSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset));
        var insSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset + 2));
        var outsSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset + 4));
        var triesSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(baseOffset + 6));
        var debugInfoOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 8));
        var insnsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(baseOffset + 12));

        var insnsStart = baseOffset + 16;
        EnsureRange(data, insnsStart, checked((int)insnsSize * 2), "code_item insns");

        var insnsSpan = data.AsSpan(insnsStart, checked((int)insnsSize * 2));
        ushort[] raw;
        if (BitConverter.IsLittleEndian)
        {


            raw = MemoryMarshal.Cast<byte, ushort>(insnsSpan).ToArray();
        }
        else
        {
            raw = new ushort[insnsSize];
            for (var i = 0; i < insnsSize; i++)
            {
                raw[i] = BinaryPrimitives.ReadUInt16LittleEndian(insnsSpan[(i * 2)..]);
            }
        }

        var code = new CodeItem
        {
            RegistersSize = registersSize,
            InsSize = insSize,
            OutsSize = outsSize,
            RawInsns = raw
        };


        if (triesSize > 0)
        {


            var triesOffset = insnsStart + checked((int)insnsSize * 2) + ((insnsSize & 1) != 0 ? 2 : 0);
            EnsureRange(data, triesOffset, checked(triesSize * 8), "code_item tries");
            ReadTriesAndHandlers(data, triesOffset, triesSize, code);
        }

        if (debugInfoOff != 0)
        {
            code.DebugInfo = ReadDebugInfoRaw(data, (int)debugInfoOff);
        }

        return code;
    }

    /// <summary>
    /// Reads the try_item array and encoded_catch_handler_list, mapping handler_off into Handlers indices.
    /// <para>读取 try_item 数组与 encoded_catch_handler_list，建立 handler_off → Handlers 索引映射。</para>
    /// </summary>
    private static void ReadTriesAndHandlers(byte[] data, int triesOffset, int triesSize, CodeItem code)
    {
        var handlersListOffset = triesOffset + triesSize * 8;
        var p = handlersListOffset;
        var handlerCount = checked((int)Leb128.ReadUleb128(data, ref p));
        var handlerByOffset = new Dictionary<int, int>(handlerCount);
        for (var i = 0; i < handlerCount; i++)
        {
            var handlerStart = p;
            handlerByOffset[handlerStart - handlersListOffset] = i;
            var handler = new CatchHandler();
            var size = Leb128.ReadSleb128(data, ref p);
            var typedCount = size < 0 ? -size : size;
            for (var t = 0; t < typedCount; t++)
            {
                var typeIdx = checked((int)Leb128.ReadUleb128(data, ref p));
                var addr = checked((int)Leb128.ReadUleb128(data, ref p));
                handler.Entries.Add(new CatchHandlerEntry { TypeIndex = typeIdx, Address = addr });
            }
            if (size <= 0)
            {
                handler.CatchAllAddress = checked((int)Leb128.ReadUleb128(data, ref p));
            }
            code.Handlers.Add(handler);
        }

        for (var i = 0; i < triesSize; i++)
        {
            var tOff = triesOffset + i * 8;
            var startAddr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tOff));
            var insnCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tOff + 4));
            var handlerOff = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tOff + 6));
            code.Tries.Add(new TryBlock
            {
                StartAddress = checked((int)startAddr),
                InsnCount = insnCount,
                HandlerIndex = handlerByOffset.TryGetValue(handlerOff, out var idx) ? idx : -1
            });
        }
    }

    /// <summary>
    /// Reads the raw debug_info_item bytes (walking the state machine to END_SEQUENCE) for verbatim rewrite.
    /// <para>读取 debug_info_item 的原始字节（按状态机扫描至 END_SEQUENCE），供重建时原样回写。</para>
    /// </summary>
    private static byte[] ReadDebugInfoRaw(byte[] data, int offset)
    {
        var start = offset;
        var p = offset;
        _ = Leb128.ReadUleb128(data, ref p);
        var parametersSize = Leb128.ReadUleb128(data, ref p);
        for (var i = 0; i < parametersSize; i++)
        {
            _ = Leb128.ReadUleb128(data, ref p);
        }
        while (true)
        {
            if (p >= data.Length) throw new DexException("debug_info_item is not END_SEQUENCE-terminated");
            var opcode = data[p++];
            switch (opcode)
            {
                case 0x00:
                    return data.AsSpan(start, p - start).ToArray();
                case 0x01: _ = Leb128.ReadUleb128(data, ref p); break;
                case 0x02: _ = Leb128.ReadSleb128(data, ref p); break;
                case 0x03:
                    _ = Leb128.ReadUleb128(data, ref p);
                    _ = Leb128.ReadUleb128(data, ref p);
                    _ = Leb128.ReadUleb128(data, ref p);
                    break;
                case 0x04:
                    _ = Leb128.ReadUleb128(data, ref p);
                    _ = Leb128.ReadUleb128(data, ref p);
                    _ = Leb128.ReadUleb128(data, ref p);
                    _ = Leb128.ReadUleb128(data, ref p);
                    break;
                case 0x05:
                case 0x06:
                    _ = Leb128.ReadUleb128(data, ref p);
                    break;
                case 0x07:
                case 0x08:
                    break;
                case 0x09: _ = Leb128.ReadUleb128(data, ref p); break;
                default: break;
            }
        }
    }

    /// <summary>
    /// Reads the annotations directory (annotations_directory_item).
    /// <para>读取注解目录（annotations_directory_item）。</para>
    /// </summary>
    private static AnnotationsDirectory ReadAnnotationsDirectory(byte[] data, int offset, DexFile dex)
    {
        var dir = new AnnotationsDirectory();
        var classAnnotationsOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        var fieldsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        var methodsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
        var paramsSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));

        if (classAnnotationsOff != 0)
        {
            dir.ClassAnnotations = ReadAnnotationSet(data, (int)classAnnotationsOff, dex);
        }

        var p = offset + 16;
        for (var i = 0; i < fieldsSize; i++)
        {
            var fieldIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
            var annOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
            p += 8;
            dir.FieldAnnotations.Add(new FieldAnnotation
            {
                FieldIndex = checked((int)fieldIdx),
                Annotations = ReadAnnotationSet(data, (int)annOff, dex)
            });
        }
        for (var i = 0; i < methodsSize; i++)
        {
            var methodIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
            var annOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
            p += 8;
            dir.MethodAnnotations.Add(new MethodAnnotation
            {
                MethodIndex = checked((int)methodIdx),
                Annotations = ReadAnnotationSet(data, (int)annOff, dex)
            });
        }
        for (var i = 0; i < paramsSize; i++)
        {
            var methodIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
            var refListOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
            p += 8;
            dir.ParameterAnnotations.Add(new ParameterAnnotation
            {
                MethodIndex = checked((int)methodIdx),
                Parameters = ReadAnnotationSetRefList(data, (int)refListOff, dex)
            });
        }
        return dir;
    }

    /// <summary>
    /// Reads an annotation set (annotation_set_item): entries are offsets to annotation items.
    /// <para>读取注解集合（annotation_set_item）：各条目为 annotation_item 的偏移。</para>
    /// </summary>
    private static AnnotationSet ReadAnnotationSet(byte[] data, int offset, DexFile dex)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        var set = new AnnotationSet();
        for (var i = 0; i < size; i++)
        {


            var itemOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4 + i * 4));
            var p = (int)itemOff;
            var visibility = data[p++];
            var item = ReadEncodedAnnotation(data, ref p, dex);
            item.Visibility = visibility;
            set.Annotations.Add(item);
        }
        return set;
    }

    /// <summary>
    /// Reads a parameter annotation ref list (annotation_set_ref_list).
    /// <para>读取参数注解引用列表（annotation_set_ref_list）。</para>
    /// </summary>
    private static AnnotationSetRefList ReadAnnotationSetRefList(byte[] data, int offset, DexFile dex)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        var list = new AnnotationSetRefList();
        for (var i = 0; i < size; i++)
        {
            var setOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4 + i * 4));
            list.Sets.Add(setOff != 0 ? ReadAnnotationSet(data, (int)setOff, dex) : null);
        }
        return list;
    }

    /// <summary>
    /// Reads an encoded annotation (encoded_annotation).
    /// <para>读取编码注解（encoded_annotation）。</para>
    /// </summary>
    private static AnnotationItem ReadEncodedAnnotation(byte[] data, ref int offset, DexFile dex)
    {
        var typeIdx = checked((int)Leb128.ReadUleb128(data, ref offset));
        var size = Leb128.ReadUleb128(data, ref offset);
        var item = new AnnotationItem { TypeIndex = typeIdx };
        for (var i = 0; i < size; i++)
        {
            var nameIdx = checked((int)Leb128.ReadUleb128(data, ref offset));
            var value = ReadEncodedValue(data, ref offset, dex);
            item.Elements.Add(new AnnotationElement { NameIndex = nameIdx, Value = value });
        }
        return item;
    }

    /// <summary>
    /// Reads an encoded value (encoded_value), decoded per the value_arg/value_type rules.
    /// <para>读取编码值（encoded_value），按 value_arg/value_type 规则解码。</para>
    /// </summary>
    private static EncodedValue ReadEncodedValue(byte[] data, ref int offset, DexFile dex)
    {
        var header = data[offset++];


        var arg = header >> 5;
        var type = (EncodedValueType)(header & 0x1f);

        switch (type)
        {
            case EncodedValueType.Byte:
                return EncodedValue.Byte(data[offset++]);
            case EncodedValueType.Short:
                return EncodedValue.Short((short)ReadSignedValue(data, ref offset, arg + 1));
            case EncodedValueType.Char:
                return EncodedValue.Char((char)ReadUnsignedValue(data, ref offset, arg + 1));
            case EncodedValueType.Int:
                return EncodedValue.Int(checked((int)ReadSignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.Long:
                return EncodedValue.Long(ReadSignedValue(data, ref offset, arg + 1));
            case EncodedValueType.Float:
                {
                    var bits = (uint)ReadUnsignedValue(data, ref offset, arg + 1);
                    return EncodedValue.Float(BitConverter.Int32BitsToSingle(checked((int)bits)));
                }
            case EncodedValueType.Double:
                {
                    var bits = ReadUnsignedValue(data, ref offset, arg + 1);
                    return EncodedValue.Double(BitConverter.Int64BitsToDouble(checked((long)bits)));
                }
            case EncodedValueType.MethodType:
                return new EncodedValue(EncodedValueType.MethodType, checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.MethodHandle:
                return new EncodedValue(EncodedValueType.MethodHandle, checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.String:
                return EncodedValue.String(checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.Type:
                return EncodedValue.TypeRef(checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.Field:
                return EncodedValue.Field(checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.Method:
                return EncodedValue.Method(checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.Enum:
                return EncodedValue.Enum(checked((int)ReadUnsignedValue(data, ref offset, arg + 1)));
            case EncodedValueType.Array:
                {
                    var size = Leb128.ReadUleb128(data, ref offset);
                    var items = new EncodedValue[size];
                    for (var i = 0; i < size; i++)
                    {
                        items[i] = ReadEncodedValue(data, ref offset, dex);
                    }
                    return EncodedValue.Array(items);
                }
            case EncodedValueType.Annotation:
                return EncodedValue.Annotation(ReadEncodedAnnotation(data, ref offset, dex));
            case EncodedValueType.Null:
                return EncodedValue.Null();
            case EncodedValueType.Boolean:
                return EncodedValue.Boolean(arg != 0);
            default:
                throw new DexException($"Unknown encoded_value type 0x{(byte)type:X2}");
        }
    }

    /// <summary>
    /// Reads an unsigned little-endian integer (up to 8 bytes).
    /// <para>读取无符号小端整数（最多 8 字节）。</para>
    /// </summary>
    private static ulong ReadUnsignedValue(byte[] data, ref int offset, int byteCount)
    {
        ulong value = 0;
        for (var i = 0; i < byteCount; i++)
        {
            value |= (ulong)data[offset + i] << (i * 8);
        }
        offset += byteCount;
        return value;
    }

    /// <summary>
    /// Reads a signed little-endian integer (up to 8 bytes, sign-extended to 64 bits).
    /// <para>读取有符号小端整数（最多 8 字节，符号扩展至 64 位）。</para>
    /// </summary>
    private static long ReadSignedValue(byte[] data, ref int offset, int byteCount)
    {
        var value = ReadUnsignedValue(data, ref offset, byteCount);
        if (byteCount < 8 && byteCount > 0 && (data[offset - 1] & 0x80) != 0)
        {
            value |= ~0UL << (byteCount * 8);
        }
        return unchecked((long)value);
    }

    private static void EnsureRange(byte[] data, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new DexException($"{what} out of range (offset=0x{offset:X}, length={length}, file={data.Length})");
        }
    }
}
