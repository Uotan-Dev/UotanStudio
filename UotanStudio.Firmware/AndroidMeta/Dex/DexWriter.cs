using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

internal static partial class DexWriter
{
    private const uint NoIndex = 0xFFFFFFFF;
    private const int HashChunkSize = 64 * 1024;
    private const long MaximumInitialCapacity = 512L * 1024 * 1024;

    /// <summary>Serializes the model into a DEX byte array.<para>把模型序列化为 DEX 字节数组。</para></summary>
    /// <param name="dex">Model to serialize; its <c>Header.Version</c> must be 035, 037, 038, 039 or 040 and method handles or call sites require 038 or newer. Header fields, table offsets and sizes are recomputed, so stored values are not reused. <see langword="null"/> is rejected.
    /// <para>待序列化的模型；其 <c>Header.Version</c> 必须为 035、037、038、039 或 040，方法句柄或调用点则要求 038 及以上。头部字段与各表的偏移和大小都会重新计算，已有取值不会被沿用。<see langword="null"/> 会被拒绝。</para></param>
    /// <returns>The little-endian DEX image bytes, with the SHA-1 signature taken over every byte after the signature field and the Adler-32 checksum taken over every byte after the checksum field both written back into the header.
    /// <para>返回小端 DEX 镜像字节：对签名字段之后的全部字节计算 SHA-1 签名、 对校验字段之后的全部字节计算 Adler-32 校验和，两者均回写到头部。</para></returns>
    public static byte[] Write(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var version = NormalizeVersion(dex.Header.Version);
        if ((dex.MethodHandles.Count > 0 || dex.CallSites.Count > 0) && string.CompareOrdinal(version, "038") < 0)
            throw new DexException("Method handles and call sites require DEX 038 or newer.");
        using var ms = new MemoryStream(EstimateCapacity(dex));
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        var headerStart = (int)ms.Position;
        w.Write(Encoding.ASCII.GetBytes("dex\n" + version + "\0"));
        w.Write(0u);
        w.Write(new byte[20]);
        w.Write(0u);
        w.Write(0x70u);
        w.Write(0x12345678u);
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write((uint)dex.Strings.Count);
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Types.Count);
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Protos.Count);
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Fields.Count);
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Methods.Count);
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Classes.Count);
        WriteOffsetPlaceholder(w);
        w.Write(0u);
        WriteOffsetPlaceholder(w);
        Align(w, 4);

        var mapItems = new List<(ushort Type, uint Size, uint Offset)>();

        var stringIdsOffset = (int)ms.Position;
        var typeIdsOffset = checked(stringIdsOffset + dex.Strings.Count * 4);
        var protoIdsOffset = checked(typeIdsOffset + dex.Types.Count * 4);
        var fieldIdsOffset = checked(protoIdsOffset + dex.Protos.Count * 12);
        var methodIdsOffset = checked(fieldIdsOffset + dex.Fields.Count * 8);
        var classDefsOffset = checked(methodIdsOffset + dex.Methods.Count * 8);
        var callSiteIdsOffset = checked(classDefsOffset + dex.Classes.Count * 32);
        var methodHandlesOffset = checked(callSiteIdsOffset + dex.CallSites.Count * 4);
        var dataStart = checked(methodHandlesOffset + dex.MethodHandles.Count * 8);
        ms.SetLength(dataStart);
        ms.Position = dataStart;
        var stringOffsets = new uint[dex.Strings.Count];
        Align(w, 4);
        var stringDataStart = (int)ms.Position;
        for (var i = 0; i < dex.Strings.Count; i++)
        {
            stringOffsets[i] = (uint)ms.Position;
            WriteStringData(w, dex.Strings[i]);
        }
        var stringDataEnd = (int)ms.Position;
        ms.Position = stringIdsOffset;
        foreach (var off in stringOffsets)
        {
            w.Write(off);
        }
        ms.Position = stringDataEnd;
        Align(w, 4);
        mapItems.Add((0x0001, (uint)dex.Strings.Count, (uint)stringIdsOffset));

        var stringIndex = new Dictionary<string, int>(dex.Strings.Count, StringComparer.Ordinal);
        for (var i = 0; i < dex.Strings.Count; i++)
        {
            stringIndex.TryAdd(dex.Strings[i], i);
        }

        var afterStrings = ms.Position;
        ms.Position = typeIdsOffset;
        foreach (var descriptor in dex.Types)
        {
            if (!stringIndex.TryGetValue(descriptor, out var idx))
            {
                throw new DexException($"String '{descriptor}' is referenced by the type table but missing from the string table");
            }
            w.Write((uint)idx);
        }
        Align(w, 4);
        mapItems.Add((0x0002, (uint)dex.Types.Count, (uint)typeIdsOffset));
        ms.Position = afterStrings;

        ms.Position = protoIdsOffset;
        for (var i = 0; i < dex.Protos.Count; i++)
        {
            w.Write((uint)dex.Protos[i].ShortyIndex);
            w.Write((uint)dex.Protos[i].ReturnTypeIndex);
            w.Write(0u);
        }
        Align(w, 4);

        var protoTypeListCount = 0;
        var firstProtoTypeList = 0u;
        var protoParamOffsets = new uint[dex.Protos.Count];
        ms.Position = afterStrings;
        for (var i = 0; i < dex.Protos.Count; i++)
        {
            if (dex.Protos[i].ParameterTypeIndices.Length > 0)
            {
                Align(w, 4);
                protoParamOffsets[i] = (uint)ms.Position;
                if (firstProtoTypeList == 0) firstProtoTypeList = protoParamOffsets[i];
                protoTypeListCount++;
                WriteTypeList(w, dex.Protos[i].ParameterTypeIndices);
            }
            else
            {
                protoParamOffsets[i] = 0;
            }
        }
        Align(w, 4);

        var protoEnd = (int)ms.Position;
        ms.Position = protoIdsOffset;
        for (var i = 0; i < dex.Protos.Count; i++)
        {
            w.Write((uint)dex.Protos[i].ShortyIndex);
            w.Write((uint)dex.Protos[i].ReturnTypeIndex);
            w.Write(protoParamOffsets[i]);
        }
        ms.Position = protoEnd;
        mapItems.Add((0x0003, (uint)dex.Protos.Count, (uint)protoIdsOffset));

        var afterProtoLists = ms.Position;
        ms.Position = fieldIdsOffset;
        foreach (var field in dex.Fields)
        {
            w.Write((ushort)field.ClassIndex);
            w.Write((ushort)field.TypeIndex);
            w.Write((uint)field.NameIndex);
        }
        Align(w, 4);
        mapItems.Add((0x0004, (uint)dex.Fields.Count, (uint)fieldIdsOffset));

        ms.Position = methodIdsOffset;
        foreach (var method in dex.Methods)
        {
            w.Write((ushort)method.ClassIndex);
            w.Write((ushort)method.ProtoIndex);
            w.Write((uint)method.NameIndex);
        }
        Align(w, 4);
        mapItems.Add((0x0005, (uint)dex.Methods.Count, (uint)methodIdsOffset));

        ms.Position = classDefsOffset;
        var classDataOffsets = new uint[dex.Classes.Count];
        var classDefPositions = new int[dex.Classes.Count];
        for (var i = 0; i < dex.Classes.Count; i++)
        {
            var cls = dex.Classes[i];
            classDefPositions[i] = (int)ms.Position;
            w.Write((uint)cls.TypeIndex);
            w.Write(cls.AccessFlags);
            w.Write(cls.SuperclassIndex < 0 ? NoIndex : (uint)cls.SuperclassIndex);

            WriteOffsetPlaceholder(w);
            w.Write(cls.SourceFileIndex < 0 ? NoIndex : (uint)cls.SourceFileIndex);
            w.Write(0u);
            WriteOffsetPlaceholder(w);
            w.Write(0u);
        }
        Align(w, 4);
        mapItems.Add((0x0006, (uint)dex.Classes.Count, (uint)classDefsOffset));

        ms.Position = methodHandlesOffset;
        foreach (var handle in dex.MethodHandles)
        {
            if (handle.Kind > 8 || handle.MemberIndex >= (handle.Kind <= 3 ? dex.Fields.Count : dex.Methods.Count))
                throw new DexException("Invalid method handle member index.");
            w.Write(handle.Kind);
            w.Write((ushort)0);
            w.Write(handle.MemberIndex);
            w.Write((ushort)0);
        }
        if (dex.MethodHandles.Count > 0) mapItems.Add((8, (uint)dex.MethodHandles.Count, (uint)methodHandlesOffset));
        if (dex.CallSites.Count > 0) mapItems.Add((7, (uint)dex.CallSites.Count, (uint)callSiteIdsOffset));
        ms.Position = afterProtoLists;

        var interfaceTypeListCount = 0;
        var firstInterfaceTypeList = 0u;
        var interfaceOffsets = new uint[dex.Classes.Count];
        for (var i = 0; i < dex.Classes.Count; i++)
        {
            var cls = dex.Classes[i];
            if (cls.InterfaceTypeIndices.Length > 0)
            {
                Align(w, 4);
                interfaceOffsets[i] = (uint)ms.Position;
                if (interfaceTypeListCount == 0) firstInterfaceTypeList = interfaceOffsets[i];
                interfaceTypeListCount++;
                WriteTypeList(w, cls.InterfaceTypeIndices);
            }
            else
            {
                interfaceOffsets[i] = 0;
            }
        }
        Align(w, 4);

        var codeItemCount = 0;
        var firstCodeItem = 0u;
        var debugPatches = new List<(int FieldPos, CodeItem Code)>();
        var methodCodeOffsets = new Dictionary<EncodedMethod, uint>();
        foreach (var method in dex.Classes.SelectMany(c => c.AllMethods))
        {
            if (method.Code is null) continue;
            Align(w, 4);
            if (codeItemCount++ == 0) firstCodeItem = (uint)ms.Position;
            methodCodeOffsets[method] = (uint)ms.Position;
            var codeStart = WriteCodeItem(w, method.Code);
            if (method.Code.DebugInfoBytes.Length > 0) debugPatches.Add((codeStart + 8, method.Code));
        }
        for (var i = 0; i < dex.Classes.Count; i++)
        {
            var cls = dex.Classes[i];
            var hasData = cls.StaticFields.Count > 0 || cls.InstanceFields.Count > 0
                          || cls.DirectMethods.Count > 0 || cls.VirtualMethods.Count > 0;
            if (!hasData)
            {
                classDataOffsets[i] = 0;
                continue;
            }

            classDataOffsets[i] = (uint)ms.Position;
            WriteUleb128(w, (uint)cls.StaticFields.Count);
            WriteUleb128(w, (uint)cls.InstanceFields.Count);
            WriteUleb128(w, (uint)cls.DirectMethods.Count);
            WriteUleb128(w, (uint)cls.VirtualMethods.Count);
            foreach (var fields in new[] { cls.StaticFields, cls.InstanceFields })
            {
                var previous = 0;
                foreach (var field in fields)
                {
                    WriteDiff(w, field.FieldIndex, ref previous);
                    WriteUleb128(w, field.AccessFlags);
                }
            }
            foreach (var methods in new[] { cls.DirectMethods, cls.VirtualMethods })
            {
                var previous = 0;
                foreach (var method in methods)
                {
                    WriteDiff(w, method.MethodIndex, ref previous);
                    WriteUleb128(w, method.AccessFlags);
                    WriteUleb128(w, methodCodeOffsets.TryGetValue(method, out var off) ? off : 0);
                }
            }
        }
        Align(w, 4);

        var debugInfoCount = 0;
        var firstDebugInfo = 0u;
        foreach (var (fieldPos, code) in debugPatches)
        {
            if (debugInfoCount == 0) firstDebugInfo = (uint)ms.Position;
            debugInfoCount++;
            var debugOff = (uint)ms.Position;
            w.Write(code.DebugInfoBytes);
            var cur = (int)ms.Position;
            ms.Position = fieldPos;
            w.Write(debugOff);
            ms.Position = cur;
        }
        Align(w, 4);

        var allItems = new List<AnnotationItem>();
        var allSets = new List<AnnotationSet>();
        var allRefLists = new List<AnnotationSetRefList>();
        var allDirs = new List<AnnotationsDirectory>();
        var seenItems = new HashSet<AnnotationItem>(ReferenceEqualityComparer.Instance);
        var seenSets = new HashSet<AnnotationSet>(ReferenceEqualityComparer.Instance);
        var seenRefLists = new HashSet<AnnotationSetRefList>(ReferenceEqualityComparer.Instance);
        foreach (var cls in dex.Classes)
        {
            var dir = cls.Annotations;
            if (dir == null || dir.IsEmpty) continue;
            allDirs.Add(dir);
            void AddSet(AnnotationSet? set)
            {
                if (set == null || set.IsEmpty) return;
                if (seenSets.Add(set)) allSets.Add(set);
                foreach (var item in set.Annotations)
                {
                    if (seenItems.Add(item)) allItems.Add(item);
                }
            }
            AddSet(dir.ClassAnnotations);
            foreach (var fa in dir.FieldAnnotations) AddSet(fa.Annotations);
            foreach (var ma in dir.MethodAnnotations) AddSet(ma.Annotations);
            foreach (var pa in dir.ParameterAnnotations)
            {
                if (seenRefLists.Add(pa.Parameters)) allRefLists.Add(pa.Parameters);
                foreach (var s in pa.Parameters.Sets) AddSet(s);
            }
        }

        var annotationItemOffsets = new Dictionary<AnnotationItem, uint>();
        foreach (var item in allItems)
        {
            annotationItemOffsets[item] = (uint)ms.Position;
            w.Write((byte)(item.Visibility & 0xFF));
            WriteEncodedAnnotation(w, item);
        }
        Align(w, 4);

        var annotationSetOffsets = new Dictionary<AnnotationSet, uint>();
        foreach (var set in allSets)
        {
            Align(w, 4);
            annotationSetOffsets[set] = (uint)ms.Position;
            w.Write((uint)set.Annotations.Count);
            foreach (var item in set.Annotations)
            {
                w.Write(annotationItemOffsets[item]);
            }
        }
        Align(w, 4);

        var refListOffsets = new Dictionary<AnnotationSetRefList, uint>();
        foreach (var rl in allRefLists)
        {
            Align(w, 4);
            refListOffsets[rl] = (uint)ms.Position;
            w.Write((uint)rl.Sets.Count);
            foreach (var set in rl.Sets)
            {
                w.Write(set != null && annotationSetOffsets.TryGetValue(set, out var off) ? off : 0u);
            }
        }
        Align(w, 4);

        var directoryOffsets = new Dictionary<AnnotationsDirectory, uint>();
        foreach (var dir in allDirs)
        {
            Align(w, 4);
            directoryOffsets[dir] = (uint)ms.Position;
            w.Write(dir.ClassAnnotations != null && annotationSetOffsets.TryGetValue(dir.ClassAnnotations, out var co) ? co : 0u);
            w.Write((uint)dir.FieldAnnotations.Count);
            w.Write((uint)dir.MethodAnnotations.Count);
            w.Write((uint)dir.ParameterAnnotations.Count);
            foreach (var fa in dir.FieldAnnotations)
            {
                w.Write((uint)fa.FieldIndex);
                w.Write(annotationSetOffsets[fa.Annotations]);
            }
            foreach (var ma in dir.MethodAnnotations)
            {
                w.Write((uint)ma.MethodIndex);
                w.Write(annotationSetOffsets[ma.Annotations]);
            }
            foreach (var pa in dir.ParameterAnnotations)
            {
                w.Write((uint)pa.MethodIndex);
                w.Write(refListOffsets[pa.Parameters]);
            }
        }
        Align(w, 4);

        var staticValueOffsets = new uint[dex.Classes.Count];
        var encodedArrayCount = 0;
        var firstEncodedArray = 0u;
        for (var i = 0; i < dex.Classes.Count; i++)
        {
            var sv = dex.Classes[i].StaticValues;
            if (sv == null || sv.Count == 0)
            {
                staticValueOffsets[i] = 0;
                continue;
            }
            staticValueOffsets[i] = (uint)ms.Position;
            if (encodedArrayCount == 0) firstEncodedArray = staticValueOffsets[i];
            encodedArrayCount++;
            WriteUleb128(w, (uint)sv.Count);
            foreach (var v in sv) WriteEncodedValue(w, v);
        }
        var callSiteOffsets = new uint[dex.CallSites.Count];
        for (var i = 0; i < dex.CallSites.Count; i++)
        {
            var values = dex.CallSites[i];
            if (values.Count < 3 || values[0].Type != EncodedValueType.MethodHandle
                || values[1].Type != EncodedValueType.String || values[2].Type != EncodedValueType.MethodType
                || Convert.ToUInt32(values[0].Value) >= dex.MethodHandles.Count
                || Convert.ToUInt32(values[1].Value) >= dex.Strings.Count
                || Convert.ToUInt32(values[2].Value) >= dex.Protos.Count
                || dex.MethodHandles[values[0].AsIndex()].Kind != 4)
                throw new DexException("Invalid call-site bootstrap arguments.");
            callSiteOffsets[i] = (uint)ms.Position;
            if (encodedArrayCount++ == 0) firstEncodedArray = (uint)ms.Position;
            WriteUleb128(w, (uint)values.Count);
            foreach (var value in values) WriteEncodedValue(w, value);
        }
        Align(w, 4);
        var classDefsEnd = (int)ms.Position;
        ms.Position = callSiteIdsOffset;
        foreach (var offset in callSiteOffsets) w.Write(offset);
        ms.Position = classDefsOffset;
        for (var i = 0; i < dex.Classes.Count; i++)
        {
            var cls = dex.Classes[i];
            ms.Position = classDefPositions[i] + 12;
            w.Write(interfaceOffsets[i]);
            ms.Position = classDefPositions[i] + 20;
            w.Write(cls.Annotations != null && directoryOffsets.TryGetValue(cls.Annotations, out var dOff) ? dOff : 0u);
            ms.Position = classDefPositions[i] + 24;
            w.Write(classDataOffsets[i]);
            ms.Position = classDefPositions[i] + 28;
            w.Write(staticValueOffsets[i]);
        }
        ms.Position = classDefsEnd;

        var dataEnd = (int)ms.Position;

        Align(w, 4);
        var mapOffset = (int)ms.Position;
        mapItems.Add((0x0000, 1, 0));
        mapItems.Add((0x1000, 1, (uint)mapOffset));

        if (stringOffsets.Length > 0) mapItems.Add((0x2002, (uint)stringOffsets.Length, stringOffsets[0]));
        var firstClassData = classDataOffsets.FirstOrDefault(o => o != 0);
        if (firstClassData != 0) mapItems.Add((0x2000, (uint)classDataOffsets.Count(o => o != 0), firstClassData));
        if (codeItemCount > 0) mapItems.Add((0x2001, (uint)codeItemCount, firstCodeItem));
        if (debugInfoCount > 0) mapItems.Add((0x2003, (uint)debugInfoCount, firstDebugInfo));
        var totalTypeListCount = protoTypeListCount + interfaceTypeListCount;
        if (totalTypeListCount > 0)
        {
            var firstTypeList = firstProtoTypeList != 0 ? firstProtoTypeList : firstInterfaceTypeList;
            mapItems.Add((0x1001, (uint)totalTypeListCount, firstTypeList));
        }
        if (annotationItemOffsets.Count > 0) mapItems.Add((0x2004, (uint)annotationItemOffsets.Count, annotationItemOffsets.Values.Min()));
        if (annotationSetOffsets.Count > 0) mapItems.Add((0x1003, (uint)annotationSetOffsets.Count, annotationSetOffsets.Values.Min()));
        if (refListOffsets.Count > 0) mapItems.Add((0x1002, (uint)refListOffsets.Count, refListOffsets.Values.Min()));
        if (directoryOffsets.Count > 0) mapItems.Add((0x2006, (uint)directoryOffsets.Count, directoryOffsets.Values.Min()));
        if (encodedArrayCount > 0) mapItems.Add((0x2005, (uint)encodedArrayCount, firstEncodedArray));

        var ordered = mapItems.Where(m => m.Size > 0).OrderBy(m => m.Offset).ToList();
        w.Write((uint)ordered.Count);
        foreach (var (type, size, offset) in ordered)
        {
            w.Write(type);
            w.Write((ushort)0);
            w.Write(size);
            w.Write(offset);
        }

        var fileSize = (int)ms.Position;

        void Patch(int pos, uint value)
        {
            ms.Position = pos;
            w.Write(value);
        }

        Patch(headerStart + 32, (uint)fileSize);
        Patch(headerStart + 52, (uint)mapOffset);
        Patch(headerStart + 60, dex.Strings.Count == 0 ? 0 : (uint)stringIdsOffset);
        Patch(headerStart + 68, dex.Types.Count == 0 ? 0 : (uint)typeIdsOffset);
        Patch(headerStart + 76, dex.Protos.Count == 0 ? 0 : (uint)protoIdsOffset);
        Patch(headerStart + 84, dex.Fields.Count == 0 ? 0 : (uint)fieldIdsOffset);
        Patch(headerStart + 92, dex.Methods.Count == 0 ? 0 : (uint)methodIdsOffset);
        Patch(headerStart + 100, dex.Classes.Count == 0 ? 0 : (uint)classDefsOffset);
        Patch(headerStart + 104, (uint)(fileSize - dataStart));
        Patch(headerStart + 108, (uint)dataStart);

        var length = checked((int)ms.Length);
        var buffer = ms.GetBuffer();

        var signature = new byte[Sha1.HashSizeInBytes];
        using (var sha1 = Sha1.CreateAccumulator())
        {
            for (var pos = 32; pos < length; pos += HashChunkSize)
            {
                sha1.Append(buffer.AsSpan(pos, Math.Min(HashChunkSize, length - pos)));
            }
            sha1.GetHashAndReset(signature);
        }
        signature.CopyTo(buffer, 12);

        var checksum = Hash.Adler32.Adler32.Compute(buffer.AsSpan(12, 20));
        for (var pos = 32; pos < length; pos += HashChunkSize)
        {
            checksum = Hash.Adler32.Adler32.Compute(buffer.AsSpan(pos, Math.Min(HashChunkSize, length - pos)), checksum);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), checksum);

        var bytes = new byte[length];
        buffer.AsSpan(0, length).CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Estimates an initial output capacity from the model's table counts, string payloads, type lists and
    /// code items so the output stream rarely has to grow and copy; annotations and deeply nested encoded
    /// values are not measured, and the stream grows on demand when the estimate falls short.
    /// <para>按模型的表数量、字符串负载、类型列表与代码项估算初始输出容量，使输出流基本无需扩容复制；
    /// 注解与深层嵌套的编码值不计入，估算不足时输出流按需扩容。</para>
    /// </summary>
    private static int EstimateCapacity(DexFile dex)
    {
        long strings = 0;
        foreach (var value in dex.Strings)
        {
            strings += Leb128.MaxEncodedBytes + Mutf8.EncodedLength(value) + 1;
        }

        var code = 0L;
        var handlers = 0L;
        var debug = 0L;
        var classData = 0L;
        foreach (var cls in dex.Classes)
        {
            classData += 4L * Leb128.MaxEncodedBytes
                + 2L * Leb128.MaxEncodedBytes * (cls.StaticFields.Count + cls.InstanceFields.Count)
                + 3L * Leb128.MaxEncodedBytes * (cls.DirectMethods.Count + cls.VirtualMethods.Count);
            foreach (var method in cls.AllMethods)
            {
                if (method.Code is not { } item) continue;
                code += 16 + 2L * item.RawInsns.Length + 8L * item.Tries.Count + 4;
                foreach (var handler in item.Handlers)
                {
                    handlers += (2L + 2L * handler.Entries.Count) * Leb128.MaxEncodedBytes;
                }
                debug += item.DebugInfoBytes.Length + 2L * Leb128.MaxEncodedBytes;
            }
        }

        var tables = 4L * dex.Strings.Count + 4L * dex.Types.Count + 12L * dex.Protos.Count
            + 8L * dex.Fields.Count + 8L * dex.Methods.Count + 32L * dex.Classes.Count
            + 4L * dex.CallSites.Count + 8L * dex.MethodHandles.Count;

        var typeLists = 0L;
        foreach (var proto in dex.Protos)
        {
            typeLists += proto.ParameterTypeIndices.Length > 0 ? 8L + 2L * proto.ParameterTypeIndices.Length : 0;
        }
        foreach (var cls in dex.Classes)
        {
            typeLists += cls.InterfaceTypeIndices.Length > 0 ? 8L + 2L * cls.InterfaceTypeIndices.Length : 0;
        }

        var values = 0L;
        foreach (var site in dex.CallSites)
        {
            values += Leb128.MaxEncodedBytes * (1L + site.Count);
        }
        foreach (var cls in dex.Classes)
        {
            if (cls.StaticValues is { Count: > 0 } staticValues)
            {
                values += Leb128.MaxEncodedBytes * (1L + staticValues.Count);
            }
        }

        var total = 0x70 + tables + strings + typeLists + code + handlers + debug + classData + values + 0x400;
        total += total / 16 + 4L * (dex.Protos.Count + dex.Classes.Count);
        return (int)Math.Min(total, MaximumInitialCapacity);
    }
}