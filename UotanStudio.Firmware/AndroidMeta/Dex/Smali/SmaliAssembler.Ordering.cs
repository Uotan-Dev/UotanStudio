using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private sealed partial class Context
    {
        private void Canonicalize()
        {
            if (Types.Count > ushort.MaxValue || Protos.Count > ushort.MaxValue)
            {
                throw new FormatException("DEX type and prototype tables cannot exceed 65535 entries.");
            }
            var strings = Reorder(Strings, StringComparer.Ordinal.Compare);
            var types = Reorder(Types, StringComparer.Ordinal.Compare);
            foreach (var proto in Protos)
            {
                proto.ShortyIndex = strings[proto.ShortyIndex];
                proto.ReturnTypeIndex = types[proto.ReturnTypeIndex];
                proto.ParameterTypeIndices = [.. proto.ParameterTypeIndices.Select(index => types[index])];
            }
            var protos = Reorder(Protos, CompareProtos);
            foreach (var field in Fields)
            {
                field.ClassIndex = types[field.ClassIndex];
                field.TypeIndex = types[field.TypeIndex];
                field.NameIndex = strings[field.NameIndex];
            }
            var fields = Reorder(Fields, (left, right) =>
                (left.ClassIndex, left.NameIndex, left.TypeIndex).CompareTo((right.ClassIndex, right.NameIndex, right.TypeIndex)));
            foreach (var method in Methods)
            {
                method.ClassIndex = types[method.ClassIndex];
                method.ProtoIndex = protos[method.ProtoIndex];
                method.NameIndex = strings[method.NameIndex];
            }
            var methods = Reorder(Methods, (left, right) =>
                (left.ClassIndex, left.NameIndex, left.ProtoIndex).CompareTo((right.ClassIndex, right.NameIndex, right.ProtoIndex)));
            foreach (var handle in MethodHandles)
            {
                var member = handle.Kind <= 3 ? fields[handle.MemberIndex] : methods[handle.MemberIndex];
                if (member > ushort.MaxValue)
                {
                    throw new FormatException("A relocated method-handle member index exceeds 16 bits.");
                }
                handle.MemberIndex = (ushort)member;
            }
            var handles = Reorder(MethodHandles, (left, right) =>
                (left.Kind, left.MemberIndex).CompareTo((right.Kind, right.MemberIndex)));
            foreach (var state in Classes)
            {
                var cls = state.Definition;
                cls.TypeIndex = types[cls.TypeIndex];
                if (cls.SuperclassIndex >= 0)
                {
                    cls.SuperclassIndex = types[cls.SuperclassIndex];
                }
                if (cls.SourceFileIndex >= 0)
                {
                    cls.SourceFileIndex = strings[cls.SourceFileIndex];
                }
                cls.InterfaceTypeIndices = [.. state.Interfaces.Select(index => types[index]).Distinct().Order()];
                foreach (var field in cls.AllFields)
                {
                    field.FieldIndex = fields[field.FieldIndex];
                }
                if (cls.AllFields.Select(field => field.FieldIndex).Distinct().Count() != cls.AllFields.Count())
                {
                    throw new FormatException("A class declares the same field more than once.");
                }
                var staticOrder = cls.StaticFields.Select((field, index) => (field.FieldIndex, index))
                    .OrderBy(item => item.FieldIndex).ToArray();
                if (cls.StaticValues is { } values)
                {
                    var valueCount = staticOrder.Length;
                    while (valueCount > 0 && !state.ExplicitStaticValues[staticOrder[valueCount - 1].index])
                    {
                        valueCount--;
                    }
                    cls.StaticValues = [.. staticOrder.Take(valueCount).Select(item => RelocateValue(values[item.index], strings, types, fields, methods, protos, handles))];
                }
                cls.StaticFields.Sort((left, right) => left.FieldIndex.CompareTo(right.FieldIndex));
                cls.InstanceFields.Sort((left, right) => left.FieldIndex.CompareTo(right.FieldIndex));
                foreach (var method in cls.AllMethods)
                {
                    method.MethodIndex = methods[method.MethodIndex];
                }
                if (cls.AllMethods.Select(method => method.MethodIndex).Distinct().Count() != cls.AllMethods.Count())
                {
                    throw new FormatException("A class declares the same method more than once.");
                }
                cls.DirectMethods.Sort((left, right) => left.MethodIndex.CompareTo(right.MethodIndex));
                cls.VirtualMethods.Sort((left, right) => left.MethodIndex.CompareTo(right.MethodIndex));
                RelocateAnnotations(cls.Annotations, strings, types, fields, methods, protos, handles);
            }
            RebuildLookups();
            CallSites.Clear();
            var methodStates = MethodStates.ToDictionary(state => state.Encoded.MethodIndex);
            foreach (var method in OrderedClasses().SelectMany(cls => cls.AllMethods))
            {
                if (method.Code is not null)
                {
                    Encode(methodStates[method.MethodIndex]);
                }
            }
        }

        private void RebuildLookups()
        {
            StringIndices.Clear();
            for (var index = 0; index < Strings.Count; index++)
            {
                StringIndices.TryAdd(Strings[index], index);
            }
            TypeIndices.Clear();
            for (var index = 0; index < Types.Count; index++)
            {
                TypeIndices.TryAdd(Types[index], index);
            }
            FieldIndices.Clear();
            for (var index = 0; index < Fields.Count; index++)
            {
                var field = Fields[index];
                FieldIndices.TryAdd((field.ClassIndex, field.NameIndex, field.TypeIndex), index);
            }
            MethodIndices.Clear();
            for (var index = 0; index < Methods.Count; index++)
            {
                var method = Methods[index];
                MethodIndices.TryAdd((method.ClassIndex, method.NameIndex, method.ProtoIndex), index);
            }
            ProtoIndices.Clear();
            for (var index = 0; index < Protos.Count; index++)
            {
                var proto = Protos[index];
                ProtoIndices.TryAdd(new ProtoKey(proto.ReturnTypeIndex, proto.ParameterTypeIndices), index);
            }
            MethodHandleIndices.Clear();
            for (var index = 0; index < MethodHandles.Count; index++)
            {
                var handle = MethodHandles[index];
                MethodHandleIndices.TryAdd((handle.Kind, handle.MemberIndex), index);
            }
        }

        private static EncodedValue RelocateValue(EncodedValue value, int[] strings, int[] types, int[] fields, int[] methods, int[] protos, int[] handles)
            => value.Type switch
            {
                EncodedValueType.String => EncodedValue.String(strings[value.AsIndex()]),
                EncodedValueType.Type => EncodedValue.TypeRef(types[value.AsIndex()]),
                EncodedValueType.Field or EncodedValueType.Enum => new(value.Type, fields[value.AsIndex()]),
                EncodedValueType.Method => EncodedValue.Method(methods[value.AsIndex()]),
                EncodedValueType.MethodType => new(value.Type, protos[value.AsIndex()]),
                EncodedValueType.MethodHandle => new(value.Type, handles[value.AsIndex()]),
                EncodedValueType.Array => EncodedValue.Array([.. value.AsArray()!.Select(item => RelocateValue(item, strings, types, fields, methods, protos, handles))]),
                EncodedValueType.Annotation => EncodedValue.Annotation(RelocateAnnotation((AnnotationItem)value.Value!, strings, types, fields, methods, protos, handles)),
                _ => value
            };

        private static AnnotationItem RelocateAnnotation(AnnotationItem item, int[] strings, int[] types, int[] fields, int[] methods, int[] protos, int[] handles)
        {
            var result = new AnnotationItem { TypeIndex = types[item.TypeIndex], Visibility = item.Visibility };
            result.Elements.AddRange(item.Elements.Select(element => new AnnotationElement
            {
                NameIndex = strings[element.NameIndex],
                Value = RelocateValue(element.Value, strings, types, fields, methods, protos, handles)
            }).OrderBy(element => element.NameIndex));
            return result;
        }

        private static void RelocateAnnotations(AnnotationsDirectory? directory, int[] strings, int[] types, int[] fields, int[] methods, int[] protos, int[] handles)
        {
            if (directory is null)
            {
                return;
            }
            void Set(AnnotationSet? set)
            {
                if (set is null)
                {
                    return;
                }
                var items = set.Annotations.Select(item => RelocateAnnotation(item, strings, types, fields, methods, protos, handles))
                    .OrderBy(item => item.TypeIndex).ToArray();
                set.Annotations.Clear();
                set.Annotations.AddRange(items);
            }
            Set(directory.ClassAnnotations);
            foreach (var field in directory.FieldAnnotations)
            {
                field.FieldIndex = fields[field.FieldIndex];
                Set(field.Annotations);
            }
            foreach (var method in directory.MethodAnnotations)
            {
                method.MethodIndex = methods[method.MethodIndex];
                Set(method.Annotations);
            }
            foreach (var parameter in directory.ParameterAnnotations)
            {
                parameter.MethodIndex = methods[parameter.MethodIndex];
                foreach (var set in parameter.Parameters.Sets)
                {
                    Set(set);
                }
            }
            directory.FieldAnnotations.Sort((left, right) => left.FieldIndex.CompareTo(right.FieldIndex));
            directory.MethodAnnotations.Sort((left, right) => left.MethodIndex.CompareTo(right.MethodIndex));
            directory.ParameterAnnotations.Sort((left, right) => left.MethodIndex.CompareTo(right.MethodIndex));
        }

        private List<ClassDefinition> OrderedClasses() => OrderedClassResult ??= ComputeOrderedClasses();

        private List<ClassDefinition> ComputeOrderedClasses()
        {
            var definitions = new Dictionary<int, ClassDefinition>();
            foreach (var state in Classes)
            {
                if (!definitions.TryAdd(state.Definition.TypeIndex, state.Definition))
                {
                    throw new FormatException("Duplicate class definition.");
                }
            }
            var result = new List<ClassDefinition>();
            var visited = new HashSet<int>();
            var visiting = new HashSet<int>();
            foreach (var definition in definitions.Values.OrderBy(item => item.TypeIndex))
            {
                var pending = new Stack<(ClassDefinition Definition, bool Exit)>();
                pending.Push((definition, false));
                while (pending.TryPop(out var item))
                {
                    var cls = item.Definition;
                    if (item.Exit)
                    {
                        visiting.Remove(cls.TypeIndex);
                        visited.Add(cls.TypeIndex);
                        result.Add(cls);
                        continue;
                    }
                    if (visited.Contains(cls.TypeIndex))
                    {
                        continue;
                    }
                    if (!visiting.Add(cls.TypeIndex))
                    {
                        throw new FormatException("Cyclic class or interface dependency.");
                    }
                    pending.Push((cls, true));
                    foreach (var dependency in cls.InterfaceTypeIndices.Prepend(cls.SuperclassIndex).Reverse())
                    {
                        if (definitions.TryGetValue(dependency, out var parent))
                        {
                            pending.Push((parent, false));
                        }
                    }
                }
            }
            return result;
        }

        private static int CompareProtos(ProtoDefinition left, ProtoDefinition right)
        {
            var result = left.ReturnTypeIndex.CompareTo(right.ReturnTypeIndex);
            if (result != 0)
            {
                return result;
            }
            return left.ParameterTypeIndices.AsSpan().SequenceCompareTo(right.ParameterTypeIndices);
        }

        private static int[] Reorder<T>(List<T> values, Comparison<T> comparison)
        {
            var indices = Enumerable.Range(0, values.Count).ToArray();
            Array.Sort(indices, (left, right) => comparison(values[left], values[right]));
            var sorted = new T[values.Count];
            var map = new int[values.Count];
            for (var index = 0; index < indices.Length; index++)
            {
                sorted[index] = values[indices[index]];
                map[indices[index]] = index;
            }
            values.Clear();
            values.AddRange(sorted);
            return map;
        }
    }
}