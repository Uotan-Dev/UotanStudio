using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class Disassembler
{
    private sealed class AnnotationIndex
    {
        public static readonly AnnotationIndex Empty = new(null);
        private readonly Dictionary<int, AnnotationSet> _fields = [];
        private readonly Dictionary<int, AnnotationSet> _methods = [];
        private readonly Dictionary<int, AnnotationSetRefList> _parameters = [];

        public AnnotationIndex(AnnotationsDirectory? directory)
        {
            if (directory is null)
            {
                return;
            }
            foreach (var row in directory.FieldAnnotations)
            {
                _fields.TryAdd(row.FieldIndex, row.Annotations);
            }
            foreach (var row in directory.MethodAnnotations)
            {
                _methods.TryAdd(row.MethodIndex, row.Annotations);
            }
            foreach (var row in directory.ParameterAnnotations)
            {
                _parameters.TryAdd(row.MethodIndex, row.Parameters);
            }
        }

        public AnnotationSet? Field(int fieldIndex) => _fields.GetValueOrDefault(fieldIndex);

        public AnnotationSet? Method(int methodIndex) => _methods.GetValueOrDefault(methodIndex);

        public AnnotationSetRefList? Parameters(int methodIndex) => _parameters.GetValueOrDefault(methodIndex);
    }

    private static string?[] ReadParameterNames(DexFile dex, CodeItem? code, int parameterCount)
    {
        var names = new string?[parameterCount];
        if (code?.DebugInfo is not { } bytes)
        {
            return names;
        }
        var offset = 0;
        Leb128.ReadUleb128(bytes, ref offset);
        var count = Leb128.ReadUleb128(bytes, ref offset);
        if (count > parameterCount)
        {
            throw new InvalidDataException("The debug parameter count exceeds the method signature.");
        }
        for (var i = 0; i < count; i++)
        {
            var name = Leb128.ReadUleb128(bytes, ref offset);
            if (name > dex.Strings.Count)
            {
                throw new InvalidDataException("A debug parameter name does not reference a string.");
            }
            if (name != 0)
            {
                names[i] = dex.Strings[(int)name - 1];
            }
        }
        return names;
    }
}