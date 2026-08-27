using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>DEX 序列化器：把对象模型重建为 DEX 字节（表顺序保持模型顺序，指令流原样复制，重算 map_list、校验和与签名）。</para>
/// DEX serializer: rebuilds the object model into DEX bytes (table order follows the model, the instruction stream
/// is copied verbatim, and the map list, checksum and signature are recomputed).
/// </summary>
internal static class DexWriter
{
    private const uint NoIndex = 0xFFFFFFFF;

    /// <summary>
    /// <para>把模型序列化为 DEX 字节数组。</para>
    /// Serializes the model into a DEX byte array.
    /// </summary>
    public static byte[] Write(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var version = NormalizeVersion(dex.Header.Version);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // ---- header（占位，随后回填）----
        // ---- header (placeholder; patched later) ----
        var headerStart = (int)ms.Position;
        w.Write(Encoding.ASCII.GetBytes("dex\n" + version + "\0")); // 8 bytes magic
        w.Write(0u);   // checksum placeholder
        w.Write(new byte[20]); // signature placeholder
        w.Write(0u);   // file_size
        w.Write(0x70u); // header_size
        w.Write(0x12345678u); // endian_tag
        w.Write(0u);   // link_size
        w.Write(0u);   // link_off
        w.Write(0u);   // map_off (placeholder)
        w.Write((uint)dex.Strings.Count); // string_ids_size
        WriteOffsetPlaceholder(w); // string_ids_off
        w.Write((uint)dex.Types.Count); // type_ids_size
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Protos.Count); // proto_ids_size
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Fields.Count); // field_ids_size
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Methods.Count); // method_ids_size
        WriteOffsetPlaceholder(w);
        w.Write((uint)dex.Classes.Count); // class_defs_size
        WriteOffsetPlaceholder(w);
        w.Write(0u); // data_size
        WriteOffsetPlaceholder(w); // data_off
        Align(w, 4);

        var mapItems = new List<(ushort Type, uint Size, uint Offset)>();

        // ---- string_ids（先写偏移数组占位，再写 string_data 并回填偏移）----
        // ---- string_ids (write the offset array as placeholders, then string_data, then patch offsets) ----
        var stringIdsOffset = (int)ms.Position;
        var stringOffsets = new uint[dex.Strings.Count];
        for (var i = 0; i < dex.Strings.Count; i++)
        {
            w.Write(0u); // 偏移占位
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

        // ---- type_ids（描述符字符串索引）----
        // ---- type_ids (descriptor string indices) ----
        var typeIdsOffset = (int)ms.Position;
        foreach (var descriptor in dex.Types)
        {
            var idx = IndexOfString(dex, descriptor);
            w.Write((uint)idx);
        }
        Align(w, 4);
        mapItems.Add((0x0002, (uint)dex.Types.Count, (uint)typeIdsOffset));

        // ---- proto_ids（12 字节/项；参数 type_list 置于表后，再回填 parameters_off）----
        // ---- proto_ids (12 bytes each; parameter type lists go after the table, then parameters_off is patched) ----
        var protoIdsOffset = (int)ms.Position;
        for (var i = 0; i < dex.Protos.Count; i++)
        {
            w.Write((uint)dex.Protos[i].ShortyIndex);
            w.Write((uint)dex.Protos[i].ReturnTypeIndex);
            w.Write(0u); // parameters_off 占位
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

        // 回填 parameters_off
        // Patch back the parameters_off fields.
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

        // ---- field_ids（class/type/name 索引）----
        // ---- field_ids (class/type/name indices) ----
        var fieldIdsOffset = (int)ms.Position;
        foreach (var field in dex.Fields)
        {
            w.Write((ushort)field.ClassIndex);
            w.Write((ushort)field.TypeIndex);
            w.Write((uint)field.NameIndex);
        }
        Align(w, 4);
        mapItems.Add((0x0004, (uint)dex.Fields.Count, (uint)fieldIdsOffset));

        // ---- method_ids（class/proto/name 索引）----
        // ---- method_ids (class/proto/name indices) ----
        var methodIdsOffset = (int)ms.Position;
        foreach (var method in dex.Methods)
        {
            w.Write((ushort)method.ClassIndex);
            w.Write((ushort)method.ProtoIndex);
            w.Write((uint)method.NameIndex);
        }
        Align(w, 4);
        mapItems.Add((0x0005, (uint)dex.Methods.Count, (uint)methodIdsOffset));

        // ---- class_defs（32 字节/项；class_data 与 code 写入数据区后回填）----
        // ---- class_defs (32 bytes each; class_data and code are written to the data section, then patched) ----
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
            // interfaces_off 占位：数据区写接口 type_list 后回填
            // interfaces_off placeholder: patched after writing the interface type list.
            WriteOffsetPlaceholder(w);
            w.Write(cls.SourceFileIndex < 0 ? NoIndex : (uint)cls.SourceFileIndex);
            w.Write(0u); // annotations_off
            WriteOffsetPlaceholder(w); // class_data_off
            w.Write(0u); // static_values_off
        }
        Align(w, 4);
        mapItems.Add((0x0006, (uint)dex.Classes.Count, (uint)classDefsOffset));

        // ---- 数据区 ----
        // ---- data section ----
        var dataStart = (int)ms.Position;

        // 接口 type_list（每个 4 字节对齐；记录数量与首个偏移供 map 0x1001 聚合条目）
        // Interface type lists (each 4-byte aligned; count and first offset are tracked for the
        // aggregated 0x1001 map entry).
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

        // class_data + code：先写代码项（4 字节对齐，记录真实 code_off），再写 class_data 体。
        // 不能先写 class_data 再用占位回填——code_off 是 uleb128，真实值长度不等（1~5 字节），
        // 回填会把 class_data 后续字节挤坏。
        // Class data and code: write code items first (4-byte aligned, recording the real code_off),
        // then the class_data body. Do NOT write the body first with placeholder code_offs — code_off
        // is a uleb128 whose real length varies (1..5 bytes), so back-patching would corrupt the rest
        // of the class_data.
        var codeItemCount = 0;
        var firstCodeItem = 0u;
        var debugPatches = new List<(int FieldPos, byte[] Bytes)>(); // (code_item 中 debug_info_off 字段位置, 原始字节)
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

            // 1) 代码项先行：code_off 在写 class_data 之前即可确定。
            // 1) Code items first, so every code_off is known before the body is written.
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
                        debugPatches.Add((codeStart + 8, dbg)); // debug_info_off 位于 code_item 偏移 8 处
                    }
                }
            }

            // 2) class_data 体紧随其后，code_off 直接写真实值。
            // 2) The class_data body follows, with real code_off values written directly.
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

        // ---- debug_info 项（原样写出；回填各 code_item 的 debug_info_off）----
        // ---- debug_info items (verbatim; each code_item's debug_info_off is patched) ----
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

        // ---- 注解区：annotation_item → annotation_set_item → annotation_set_ref_list → annotations_directory_item ----
        // 先收集全部引用的注解对象（按引用去重），再按依赖顺序写出。
        // ---- Annotations: collect every referenced annotation object (reference-deduped), then write
        // in dependency order: annotation_item → annotation_set_item → annotation_set_ref_list →
        // annotations_directory_item. ----
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

        // 1) annotation_item（encoded_annotation；无对齐要求；首字节为 visibility）
        // 1) annotation_item (encoded_annotation; no alignment requirement; leading byte is visibility).
        var annotationItemOffsets = new Dictionary<AnnotationItem, uint>();
        foreach (var item in allItems)
        {
            annotationItemOffsets[item] = (uint)ms.Position;
            w.Write((byte)(item.Visibility & 0xFF));
            WriteEncodedAnnotation(w, item);
        }
        Align(w, 4);

        // 2) annotation_set_item（4 字节对齐；条目为纯偏移，不打包 visibility）
        // 2) annotation_set_item (4-byte aligned; entries are plain offsets, no visibility packing).
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

        // 3) annotation_set_ref_list（4 字节对齐；无注解的参数写 0）
        // 3) annotation_set_ref_list (4-byte aligned; 0 for parameters without annotations).
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

        // 4) annotations_directory_item（4 字节对齐）
        // 4) annotations_directory_item (4-byte aligned).
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

        // ---- 静态初始值（encoded_array；回填 class_defs 的 static_values_off）----
        // ---- Static values (encoded_array; class_defs' static_values_off is patched) ----
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

        // 回填 class_defs：interfaces_off / annotations_off / class_data_off / static_values_off
        // Patch class_defs: interfaces_off / annotations_off / class_data_off / static_values_off.
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

        // ---- map_list（置于数据区末尾；按初始偏移升序，补全 code_item/type_list/debug_info/annotation 条目）----
        // ---- map_list (at the end of the data section; ascending by initial offset; code_item,
        // type_list, debug_info and annotation entries included) ----
        Align(w, 4);
        var mapOffset = (int)ms.Position;
        mapItems.Add((0x0000, 1, 0)); // header_item
        mapItems.Add((0x1000, 1, (uint)mapOffset)); // map_list
        // 字符串数据与代码/类数据合并条目（类型代码 0x2002 / 0x2001 / 0x2000 按需）
        // Aggregated entries for string data, code and class data.
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

        // 按初始偏移升序写出（与真实 dx/d8 产物一致）
        // Write in ascending initial-offset order (matching real dx/d8 output).
        var ordered = mapItems.OrderBy(m => m.Offset).ToList();
        w.Write((uint)ordered.Count);
        foreach (var (type, size, offset) in ordered)
        {
            w.Write(type);
            w.Write((ushort)0); // unused
            w.Write(size);
            w.Write(offset);
        }

        var fileSize = (int)ms.Position;

        // ---- 回填头部偏移（header 布局：magic@0 checksum@8 signature@12 file_size@32 header_size@36
        // endian@40 link@44/48 map_off@52 各表 size@56/64/72/80/88/96 off@60/68/76/84/92/100 data@104/108）----
        // ---- Patch header offsets (layout: magic@0 checksum@8 signature@12 file_size@32 header_size@36
        // endian@40 link@44/48 map_off@52, table sizes@56/64/72/80/88/96, offsets@60/68/76/84/92/100, data@104/108) ----
        void Patch(int pos, uint value)
        {
            ms.Position = pos;
            w.Write(value);
        }

        Patch(headerStart + 32, (uint)fileSize);
        Patch(headerStart + 52, (uint)mapOffset);          // map_off
        Patch(headerStart + 60, (uint)stringIdsOffset);    // string_ids_off
        Patch(headerStart + 68, (uint)typeIdsOffset);      // type_ids_off
        Patch(headerStart + 76, (uint)protoIdsOffset);     // proto_ids_off
        Patch(headerStart + 84, (uint)fieldIdsOffset);     // field_ids_off
        Patch(headerStart + 92, (uint)methodIdsOffset);    // method_ids_off
        Patch(headerStart + 100, (uint)classDefsOffset);   // class_defs_off
        Patch(headerStart + 104, (uint)(dataEnd - dataStart)); // data_size
        Patch(headerStart + 108, (uint)dataStart);         // data_off

        var bytes = ms.ToArray();

        // ---- 签名（字节 32..文件尾 的 SHA-1）----
        // ---- Signature (SHA-1 of bytes 32..EOF) ----
        var signature = SHA1.HashData(bytes.AsSpan(32));
        signature.CopyTo(bytes, 12);

        // ---- 校验和（字节 12..文件尾 的 Adler-32）----
        // ---- Checksum (Adler-32 of bytes 12..EOF) ----
        var checksum = Adler32(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checksum);

        return bytes;
    }

    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrEmpty(version) || version.Length != 3)
        {
            throw new DexException($"Invalid DEX version '{version}' (expected 3 digits, e.g. 035)");
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
        WriteUleb128(w, (uint)value.Length); // utf16_size（UTF-16 代码单元数）
        w.Write(mutf8);
        w.Write((byte)0); // 终止 NUL
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
    /// <para>写出 code_item（含异常保护块与处理器列表）；返回 code_item 起始位置，供回填 debug_info_off。</para>
    /// Writes a code_item (including tries and the catch handler list); returns the code_item start
    /// position so the caller can patch debug_info_off.
    /// </summary>
    private static int WriteCodeItem(BinaryWriter w, CodeItem code)
    {
        var start = (int)w.BaseStream.Position;
        w.Write(code.RegistersSize);
        w.Write(code.InsSize);
        w.Write(code.OutsSize);
        w.Write((ushort)code.Tries.Count); // tries_size
        w.Write(0u);                        // debug_info_off 占位（随后回填）
        w.Write((uint)code.RawInsns.Length); // insns_size（16 位单元数）
        foreach (var unit in code.RawInsns)
        {
            w.Write(unit);
        }

        if (code.Tries.Count > 0)
        {
            // 对齐到 4 字节（insns 为奇数单元时补 2 字节），随后写 try_item 数组与处理器列表。
            // Pad to 4-byte alignment (2 bytes when insns has an odd unit count), then write the
            // try_item array and the catch handler list.
            Align(w, 4);

            // 先把处理器列表编码到临时流，得到每个 handler 相对列表起始（size 字段）的偏移（handler_off）。
            // Encode the handler list into a temp stream first to learn each handler's offset
            // relative to the list start (the size field), which is what handler_off means.
            using var hms = new MemoryStream();
            using var hw = new BinaryWriter(hms, Encoding.UTF8, leaveOpen: true);
            var listHeaderSize = Leb128.Uleb128Size((uint)code.Handlers.Count);
            var handlerOffsets = new int[code.Handlers.Count];
            for (var i = 0; i < code.Handlers.Count; i++)
            {
                handlerOffsets[i] = (int)hms.Position + listHeaderSize;
                WriteCatchHandler(hw, code.Handlers[i]);
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

            // encoded_catch_handler_list：uleb128 数量 + 各 handler。
            WriteUleb128(w, (uint)code.Handlers.Count);
            w.Write(hms.ToArray());
        }
        return start;
    }

    /// <summary>
    /// <para>写出单个 encoded_catch_handler（sleb128 类型数 + 类型/地址对 + 可选 catch_all）。</para>
    /// Writes a single encoded_catch_handler (sleb128 type count + type/address pairs + optional catch-all).
    /// </summary>
    private static void WriteCatchHandler(BinaryWriter w, CatchHandler handler)
    {
        var typedCount = handler.Entries.Count;
        // size <= 0 表示类型化条目后跟 catch_all（0 表示纯 catch-all）。
        // A non-positive size means typed entries are followed by a catch-all (0 = catch-all only).
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
    /// <para>写出编码值（encoded_value），按 value_arg/value_type 极小编码。</para>
    /// Writes an encoded value (encoded_value), minimally encoded per value_arg/value_type.
    /// </summary>
    private static void WriteEncodedValue(BinaryWriter w, EncodedValue value)
    {
        switch (value.Type)
        {
            case EncodedValueType.Byte:
                w.Write((byte)EncodedValueType.Byte); // arg=0
                w.Write(value.AsInt());
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
                    var n = UnsignedByteCount(bits, 4);
                    w.Write((byte)((n - 1) << 5 | (byte)EncodedValueType.Float));
                    for (var i = 0; i < n; i++) w.Write((byte)(bits >> (i * 8)));
                    break;
                }
            case EncodedValueType.Double:
                {
                    var bits = BitConverter.DoubleToUInt64Bits((double)(value.Value ?? 0d));
                    var n = UnsignedByteCount(bits, 8);
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
                    w.Write((byte)EncodedValueType.Array); // arg=0
                    var items = value.AsArray() ?? [];
                    WriteUleb128(w, (uint)items.Length);
                    foreach (var item in items) WriteEncodedValue(w, item);
                    break;
                }
            case EncodedValueType.Annotation:
                {
                    w.Write((byte)EncodedValueType.Annotation); // arg=0
                    var item = value.Value as AnnotationItem;
                    WriteEncodedAnnotation(w, item ?? new AnnotationItem());
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
    /// <para>写出编码注解（encoded_annotation）。</para>
    /// Writes an encoded annotation (encoded_annotation).
    /// </summary>
    private static void WriteEncodedAnnotation(BinaryWriter w, AnnotationItem item)
    {
        WriteUleb128(w, (uint)item.TypeIndex);
        WriteUleb128(w, (uint)item.Elements.Count);
        foreach (var element in item.Elements)
        {
            WriteUleb128(w, (uint)element.NameIndex);
            WriteEncodedValue(w, element.Value);
        }
    }

    /// <summary>
    /// <para>计算有符号值以符号扩展表示所需的最小字节数（≤ <paramref name="maxBytes"/>）。</para>
    /// Computes the minimum byte count (≤ <paramref name="maxBytes"/>) to represent a signed value by sign extension.
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
    /// <para>计算无符号值右零扩展表示所需的最小字节数（≤ <paramref name="maxBytes"/>）。</para>
    /// Computes the minimum byte count (≤ <paramref name="maxBytes"/>) to represent an unsigned value by right zero extension.
    /// </summary>
    private static int UnsignedByteCount(ulong v, int maxBytes)
    {
        var n = maxBytes;
        while (n > 1 && (v >> (8 * (n - 1))) == 0) n--;
        return n;
    }

    private static int IndexOfString(DexFile dex, string value)
    {
        var idx = dex.Strings.IndexOf(value);
        if (idx < 0)
        {
            throw new DexException($"String '{value}' is referenced by the type table but missing from the string table");
        }
        return idx;
    }

    private static void WriteDiff(BinaryWriter w, int index, ref int previous)
    {
        WriteUleb128(w, (uint)(index - previous));
        previous = index;
    }

    private static void WriteUleb128(BinaryWriter w, uint value)
    {
        var buffer = new byte[5];
        var end = Leb128.WriteUleb128(buffer, 0, value);
        w.Write(buffer, 0, end);
    }

    private static void WriteSleb128(BinaryWriter w, int value)
    {
        var buffer = new byte[5];
        var end = Leb128.WriteSleb128(buffer, 0, value);
        w.Write(buffer, 0, end);
    }

    private static void WriteOffsetPlaceholder(BinaryWriter w) => w.Write(0u);

    private static void Align(BinaryWriter w, int alignment)
    {
        var pad = (alignment - ((int)w.BaseStream.Position % alignment)) % alignment;
        for (var i = 0; i < pad; i++) w.Write((byte)0);
    }

    /// <summary>
    /// <para>Adler-32 校验和（RFC 1950）。</para>
    /// Adler-32 checksum (RFC 1950).
    /// </summary>
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint mod = 65521;
        var a = 1u;
        var b = 0u;
        foreach (var t in data)
        {
            a = (a + t) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }
}