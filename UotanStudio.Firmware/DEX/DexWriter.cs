using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex;

internal static partial class DexWriter
{
    private const uint NoIndex = 0xFFFFFFFF;

    /// <summary>
    /// Serializes the model into a DEX byte array.
    /// <para>把模型序列化为 DEX 字节数组。</para>
    /// </summary>
    public static byte[] Write(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var version = NormalizeVersion(dex.Header.Version);
        using var ms = new MemoryStream();
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
        var stringOffsets = new uint[dex.Strings.Count];
        for (var i = 0; i < dex.Strings.Count; i++)
        {
            w.Write(0u);
        }
        Align(w, 4);
        var stringDataStart = (int)ms.Position;
        for (var i = 0; i < dex.Strings.Count; i++)
        {
            stringOffsets[i] = (uint)ms.Position;
            WriteStringData(w, dex.Strings[i]);
            Align(w, 4);
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

        var typeIdsOffset = (int)ms.Position;
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


        var protoIdsOffset = (int)ms.Position;
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


        var fieldIdsOffset = (int)ms.Position;
        foreach (var field in dex.Fields)
        {
            w.Write((ushort)field.ClassIndex);
            w.Write((ushort)field.TypeIndex);
            w.Write((uint)field.NameIndex);
        }
        Align(w, 4);
        mapItems.Add((0x0004, (uint)dex.Fields.Count, (uint)fieldIdsOffset));


        var methodIdsOffset = (int)ms.Position;
        foreach (var method in dex.Methods)
        {
            w.Write((ushort)method.ClassIndex);
            w.Write((ushort)method.ProtoIndex);
            w.Write((uint)method.NameIndex);
        }
        Align(w, 4);
        mapItems.Add((0x0005, (uint)dex.Methods.Count, (uint)methodIdsOffset));


        var classDefsOffset = (int)ms.Position;
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


        var dataStart = (int)ms.Position;


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
        var debugPatches = new List<(int FieldPos, byte[] Bytes)>();
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


            Align(w, 4);
            var methodCodeOffsets = new Dictionary<EncodedMethod, uint>();
            foreach (var m in cls.AllMethods)
            {
                if (m.Code != null)
                {
                    Align(w, 4);
                    if (codeItemCount == 0) firstCodeItem = (uint)ms.Position;
                    codeItemCount++;
                    methodCodeOffsets[m] = (uint)ms.Position;
                    var codeStart = WriteCodeItem(w, m.Code);
                    if (m.Code.DebugInfo is { Length: > 0 } dbg)
                    {
                        debugPatches.Add((codeStart + 8, dbg));
                    }
                }
            }


            classDataOffsets[i] = (uint)ms.Position;
            WriteUleb128(w, (uint)cls.StaticFields.Count);
            WriteUleb128(w, (uint)cls.InstanceFields.Count);
            WriteUleb128(w, (uint)cls.DirectMethods.Count);
            WriteUleb128(w, (uint)cls.VirtualMethods.Count);
            var prev = 0;
            foreach (var f in cls.StaticFields.Concat(cls.InstanceFields))
            {
                WriteDiff(w, f.FieldIndex, ref prev);
                WriteUleb128(w, f.AccessFlags);
            }
            prev = 0;
            foreach (var m in cls.DirectMethods.Concat(cls.VirtualMethods))
            {
                WriteDiff(w, m.MethodIndex, ref prev);
                WriteUleb128(w, m.AccessFlags);
                WriteUleb128(w, methodCodeOffsets.TryGetValue(m, out var off) ? off : 0);
            }
        }
        Align(w, 4);


        var debugInfoCount = 0;
        var firstDebugInfo = 0u;
        foreach (var (fieldPos, dbgBytes) in debugPatches)
        {
            Align(w, 4);
            if (debugInfoCount == 0) firstDebugInfo = (uint)ms.Position;
            debugInfoCount++;
            var debugOff = (uint)ms.Position;
            w.Write(dbgBytes);
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
        foreach (var cls in dex.Classes)
        {
            var dir = cls.Annotations;
            if (dir == null || dir.IsEmpty) continue;
            allDirs.Add(dir);
            void AddSet(AnnotationSet? set)
            {
                if (set == null || set.IsEmpty) return;
                if (!allSets.Contains(set)) allSets.Add(set);
                foreach (var item in set.Annotations)
                {
                    if (!allItems.Contains(item)) allItems.Add(item);
                }
            }
            AddSet(dir.ClassAnnotations);
            foreach (var fa in dir.FieldAnnotations) AddSet(fa.Annotations);
            foreach (var ma in dir.MethodAnnotations) AddSet(ma.Annotations);
            foreach (var pa in dir.ParameterAnnotations)
            {
                if (!allRefLists.Contains(pa.Parameters)) allRefLists.Add(pa.Parameters);
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
            Align(w, 4);
            staticValueOffsets[i] = (uint)ms.Position;
            if (encodedArrayCount == 0) firstEncodedArray = staticValueOffsets[i];
            encodedArrayCount++;
            WriteUleb128(w, (uint)sv.Count);
            foreach (var v in sv) WriteEncodedValue(w, v);
        }
        Align(w, 4);


        var classDefsEnd = (int)ms.Position;
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


        var ordered = mapItems.OrderBy(m => m.Offset).ToList();
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
        Patch(headerStart + 60, (uint)stringIdsOffset);
        Patch(headerStart + 68, (uint)typeIdsOffset);
        Patch(headerStart + 76, (uint)protoIdsOffset);
        Patch(headerStart + 84, (uint)fieldIdsOffset);
        Patch(headerStart + 92, (uint)methodIdsOffset);
        Patch(headerStart + 100, (uint)classDefsOffset);
        Patch(headerStart + 104, (uint)(dataEnd - dataStart));
        Patch(headerStart + 108, (uint)dataStart);

        var bytes = ms.ToArray();


        var signature = SHA1.HashData(bytes.AsSpan(32));
        signature.CopyTo(bytes, 12);


        var checksum = Adler32(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checksum);

        return bytes;
    }
}
