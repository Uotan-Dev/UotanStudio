using System.Globalization;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Smali;

public static partial class SmaliAssembler
{
    private sealed record ParameterDeclaration(string Register, string? Name, AnnotationSet Annotations);

    private sealed partial class Context
    {
        public AnnotationItem ReadAnnotation(string[] lines, ref int index, ClassState cls)
        {
            var tokens = new List<string>();
            for (; index < lines.Length; index++)
            {
                tokens.AddRange(SmaliSyntax.AnnotationTokens(lines[index]));
                if (tokens.Count >= 2 && tokens[^2] == ".end" && tokens[^1] == "annotation")
                {
                    return new AnnotationParser(this, Types[cls.Definition.TypeIndex], tokens).Parse();
                }
            }
            throw new FormatException("An annotation declaration is missing .end annotation.");
        }

        public void AddClassAnnotation(ClassState cls, AnnotationItem item)
        {
            var directory = cls.Definition.Annotations ??= new AnnotationsDirectory();
            AddAnnotation(directory.ClassAnnotations ??= new AnnotationSet(), item);
        }

        public void AddMethodAnnotation(MethodState method, AnnotationItem item)
        {
            var directory = method.Class.Definition.Annotations ??= new AnnotationsDirectory();
            var entry = directory.MethodAnnotations.FirstOrDefault(value => value.MethodIndex == method.Encoded.MethodIndex);
            if (entry is null)
            {
                entry = new MethodAnnotation { MethodIndex = method.Encoded.MethodIndex };
                directory.MethodAnnotations.Add(entry);
            }
            AddAnnotation(entry.Annotations, item);
        }

        public void ReadFieldAnnotations(ClassState cls, EncodedField field, string[] lines, ref int index)
        {
            var annotations = FollowingAnnotations(lines, ref index, cls);
            if (index + 1 < lines.Length && lines[index + 1] == ".end field")
            {
                index++;
                if (!annotations.IsEmpty)
                {
                    var directory = cls.Definition.Annotations ??= new AnnotationsDirectory();
                    directory.FieldAnnotations.Add(new FieldAnnotation { FieldIndex = field.FieldIndex, Annotations = annotations });
                }
            }
            else
            {
                foreach (var annotation in annotations.Annotations)
                {
                    AddClassAnnotation(cls, annotation);
                }
            }
        }

        public void ReadParameter(MethodState method, string[] lines, ref int index)
        {
            var operands = SmaliSyntax.Operands(Token(lines[index], ".param"));
            if (operands.Length is < 1 or > 2)
            {
                throw new FormatException("A parameter directive requires a register and an optional quoted name.");
            }
            var name = operands.Length == 2 ? SmaliSyntax.StringLiteral(operands[1]) : null;
            var annotations = FollowingAnnotations(lines, ref index, method.Class);
            if (index + 1 < lines.Length && lines[index + 1] == ".end param")
            {
                index++;
            }
            else
            {
                foreach (var annotation in annotations.Annotations)
                {
                    AddMethodAnnotation(method, annotation);
                }
                annotations = new AnnotationSet();
            }
            method.Parameters.Add(new ParameterDeclaration(operands[0], name, annotations));
        }

        private AnnotationSet FollowingAnnotations(string[] lines, ref int index, ClassState cls)
        {
            var set = new AnnotationSet();
            while (index + 1 < lines.Length && lines[index + 1].StartsWith(".annotation ", StringComparison.Ordinal))
            {
                index++;
                AddAnnotation(set, ReadAnnotation(lines, ref index, cls));
            }
            return set;
        }

        private static void AddAnnotation(AnnotationSet set, AnnotationItem item)
        {
            if (set.Annotations.Any(existing => existing.TypeIndex == item.TypeIndex))
            {
                throw new FormatException("An annotation set cannot contain duplicate annotation types.");
            }
            set.Annotations.Add(item);
        }

        private void ResolveParameters(MethodState state)
        {
            var types = Protos[Methods[state.Encoded.MethodIndex].ProtoIndex].ParameterTypeIndices;
            var slots = new Dictionary<int, int>();
            var word = (state.Encoded.AccessFlags & AccessFlags.Static) == 0 ? 1 : 0;
            for (var index = 0; index < types.Length; index++)
            {
                slots.Add(word, index);
                word += Types[types[index]] is "J" or "D" ? 2 : 1;
            }
            var seen = new HashSet<int>();
            ParameterAnnotation? annotation = null;
            foreach (var parameter in state.Parameters)
            {
                var register = parameter.Register;
                if (register.Length < 2 || register[0] is not ('p' or 'v')
                    || !int.TryParse(register.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    throw new FormatException("Invalid parameter register.");
                }
                var slot = register[0] == 'p' ? number : number - (state.Code.RegistersSize - state.Code.InsSize);
                if (!slots.TryGetValue(slot, out var index) || !seen.Add(index))
                {
                    throw new FormatException("A parameter must name the first word of a distinct declared parameter.");
                }
                if (parameter.Name is not null && (state.Encoded.AccessFlags & (AccessFlags.Abstract | AccessFlags.Native)) == 0)
                {
                    String(parameter.Name);
                    state.ParameterNames.Add(index, parameter.Name);
                }
                if (parameter.Annotations.IsEmpty)
                {
                    continue;
                }
                if (annotation is null)
                {
                    annotation = new ParameterAnnotation { MethodIndex = state.Encoded.MethodIndex };
                    annotation.Parameters.Sets.AddRange(Enumerable.Repeat<AnnotationSet?>(null, types.Length));
                    (state.Class.Definition.Annotations ??= new AnnotationsDirectory()).ParameterAnnotations.Add(annotation);
                }
                annotation.Parameters.Sets[index] = parameter.Annotations;
            }
        }

        private void EncodeParameterNames(MethodState state)
        {
            if (state.ParameterNames.Count == 0)
            {
                return;
            }
            var count = Protos[Methods[state.Encoded.MethodIndex].ProtoIndex].ParameterTypeIndices.Length;
            using var stream = new MemoryStream();
            void Write(uint value)
            {
                Span<byte> bytes = stackalloc byte[5];
                var size = Leb128.WriteUleb128(bytes, 0, value);
                stream.Write(bytes[..size]);
            }
            Write(0);
            Write((uint)count);
            for (var i = 0; i < count; i++)
            {
                Write(state.ParameterNames.TryGetValue(i, out var name) ? checked((uint)String(name) + 1) : 0);
            }
            stream.WriteByte(0);
            state.Code.DebugInfo = stream.ToArray();
        }
        private sealed class AnnotationParser(Context context, string owner, List<string> tokens)
        {
            private int _position;

            public EncodedValue ParseValue()
            {
                var value = ReadValue(0);
                if (_position != tokens.Count)
                {
                    throw new FormatException("Unexpected tokens after an encoded value.");
                }
                return value;
            }

            public AnnotationItem Parse()
            {
                Expect(".annotation");
                var visibility = Take() switch
                {
                    "build" => AnnotationVisibility.Build,
                    "runtime" => AnnotationVisibility.Runtime,
                    "system" => AnnotationVisibility.System,
                    _ => throw new FormatException("Unknown annotation visibility.")
                };
                var item = ReadItem("annotation", 0);
                item.Visibility = visibility;
                if (_position != tokens.Count)
                {
                    throw new FormatException("Unexpected tokens after an annotation.");
                }
                return item;
            }

            private AnnotationItem ReadItem(string terminator, int depth)
            {
                CheckDepth(depth);
                var descriptor = SmaliTypes.Single(Take(), allowVoid: false);
                if (!descriptor.StartsWith('L'))
                {
                    throw new FormatException("An annotation type must be a class descriptor.");
                }
                var item = new AnnotationItem { TypeIndex = context.Type(descriptor) };
                var names = new HashSet<int>();
                while (Peek() != ".end")
                {
                    var name = Take();
                    if (name is "{" or "}" or "=" or "," || name.StartsWith('.') || name.StartsWith('"') || name.StartsWith('\''))
                    {
                        throw new FormatException("Invalid annotation element name.");
                    }
                    var nameIndex = context.String(name);
                    if (!names.Add(nameIndex))
                    {
                        throw new FormatException("An annotation contains a duplicate element name.");
                    }
                    Expect("=");
                    item.Elements.Add(new AnnotationElement { NameIndex = nameIndex, Value = ReadValue(depth + 1) });
                }
                Expect(".end");
                Expect(terminator);
                return item;
            }

            private EncodedValue ReadValue(int depth)
            {
                CheckDepth(depth);
                var token = Take();
                if (token == ".subannotation")
                {
                    return EncodedValue.Annotation(ReadItem("subannotation", depth + 1));
                }
                if (token == "{")
                {
                    var values = new List<EncodedValue>();
                    if (Peek() != "}")
                    {
                        while (true)
                        {
                            values.Add(ReadValue(depth + 1));
                            if (Peek() == "}")
                            {
                                break;
                            }
                            Expect(",");
                        }
                    }
                    Expect("}");
                    return EncodedValue.Array([.. values]);
                }
                if (token.StartsWith('"'))
                {
                    return EncodedValue.String(context.String(SmaliSyntax.StringLiteral(token)));
                }
                if (token.StartsWith('\''))
                {
                    return EncodedValue.Char(SmaliSyntax.CharacterLiteral(token));
                }
                if (token == ".enum")
                {
                    return EncodedValue.Enum(context.ResolveReference(Member(Take()), RefKind.Field));
                }
                if (token == "null")
                {
                    return EncodedValue.Null();
                }
                if (token is "true" or "false")
                {
                    return EncodedValue.Boolean(token == "true");
                }
                var handleSeparator = token.IndexOf('@');
                if (handleSeparator > 0 && !token.StartsWith('L') && !token.StartsWith('['))
                {
                    return new EncodedValue(EncodedValueType.MethodHandle,
                        context.MethodHandle(token[..(handleSeparator + 1)] + Member(token[(handleSeparator + 1)..])));
                }
                if (token.StartsWith('('))
                {
                    if (context.MinimumVersion == "035")
                    {
                        context.MinimumVersion = "038";
                    }
                    return new EncodedValue(EncodedValueType.MethodType, context.Prototype(token));
                }
                if (token.Contains("->", StringComparison.Ordinal) || token.Contains(':') || token.Contains('('))
                {
                    var kind = token.Contains('(') ? RefKind.Method : RefKind.Field;
                    return new EncodedValue(kind == RefKind.Method ? EncodedValueType.Method : EncodedValueType.Field,
                        context.ResolveReference(Member(token), kind));
                }
                var first = token[0];
                if (first is 'L' or '[' || token.Length == 1 && "VZBSCIJFD".Contains(first))
                {
                    return EncodedValue.TypeRef(context.Type(SmaliTypes.Single(token, allowVoid: true)));
                }
                return SmaliLiterals.Scalar(token);
            }

            private string Member(string value) => value.Contains("->", StringComparison.Ordinal) ? value : owner + "->" + value;
            private string Peek() => _position < tokens.Count ? tokens[_position] : throw new FormatException("Incomplete annotation value.");
            private string Take() { var token = Peek(); _position++; return token; }
            private void Expect(string value)
            {
                if (Take() != value)
                {
                    throw new FormatException($"Expected '{value}' in annotation.");
                }
            }
            private static void CheckDepth(int depth)
            {
                if (depth > 64)
                {
                    throw new FormatException("Annotation nesting exceeds the supported depth of 64.");
                }
            }
        }
    }
}