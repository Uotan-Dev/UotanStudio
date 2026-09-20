using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DEX 高级特性 round-trip 测试：异常表（tries/handlers）、静态字段初始值（encoded_array）、
/// 注解（annotation_set_item / encoded_annotation / visibility）与 debug_info 原始字节。
/// <para>Advanced DEX round-trip tests: exception tables (tries/handlers), static field initial
/// values (encoded_array), annotations (annotation_set_item / encoded_annotation / visibility)
/// and raw debug_info bytes.</para>
/// </summary>
public class DexAdvancedRoundTripTests
{
    [Fact]
    public void TryCatch_RoundTripsTriesAndHandlers()
    {
        var dex = BuildTryCatchDex();
        var bytes = dex.ToArray();
        var loaded = DexFile.Load(bytes);

        var cls = loaded.Classes[0];
        var method = cls.DirectMethods[0];
        Assert.NotNull(method.Code);
        var code = method.Code!;

        Assert.Single(code.Tries);
        Assert.Single(code.Handlers);
        Assert.Equal(1, code.Tries[0].StartAddress);
        Assert.Equal(1, code.Tries[0].InsnCount);
        Assert.Equal(0, code.Tries[0].HandlerIndex);

        var handler = code.Handlers[0];
        Assert.Single(handler.Entries);
        Assert.Equal(2, handler.Entries[0].Address);
        Assert.Equal("Ljava/lang/Exception;", loaded.GetType(handler.Entries[0].TypeIndex));
        Assert.False(handler.HasCatchAll);
    }

    [Fact]
    public void CatchAll_RoundTrips()
    {
        var dex = BuildCatchAllDex();
        var loaded = DexFile.Load(dex.ToArray());

        var code = loaded.Classes[0].DirectMethods[0].Code!;
        var handler = code.Handlers[0];
        Assert.Empty(handler.Entries);
        Assert.True(handler.HasCatchAll);
        Assert.Equal(2, handler.CatchAllAddress);
    }

    [Fact]
    public void StaticValues_RoundTripsEncodedArray()
    {
        var dex = BuildStaticValuesDex();
        var loaded = DexFile.Load(dex.ToArray());

        var cls = loaded.Classes[0];
        Assert.NotNull(cls.StaticValues);
        Assert.Equal(2, cls.StaticValues!.Count);
        Assert.Equal(EncodedValueType.Int, cls.StaticValues[0].Type);
        Assert.Equal(42, cls.StaticValues[0].AsInt());
        Assert.Equal(EncodedValueType.String, cls.StaticValues[1].Type);
        Assert.Equal("hello", loaded.GetString(cls.StaticValues[1].AsIndex()));
    }

    [Fact]
    public void Annotations_RoundTripsAllScopesAndVisibility()
    {
        var dex = BuildAnnotationsDex();
        var loaded = DexFile.Load(dex.ToArray());

        var cls = loaded.Classes[0];
        Assert.NotNull(cls.Annotations);
        var dir = cls.Annotations!;

        // 类级注解 + runtime 可见性
        Assert.NotNull(dir.ClassAnnotations);
        var classAnn = Assert.Single(dir.ClassAnnotations!.Annotations);
        Assert.Equal(AnnotationVisibility.Runtime, classAnn.Visibility);
        Assert.Equal("Lcom/example/Ann;", loaded.GetType(classAnn.TypeIndex));
        var elem = Assert.Single(classAnn.Elements);
        Assert.Equal("value", loaded.GetString(elem.NameIndex));
        Assert.Equal("class-ann", loaded.GetString(elem.Value.AsIndex()));

        // 字段注解
        var fieldAnn = Assert.Single(dir.FieldAnnotations);
        Assert.Equal(AnnotationVisibility.System, fieldAnn.Annotations.Annotations[0].Visibility);

        // 方法注解
        var methodAnn = Assert.Single(dir.MethodAnnotations);
        Assert.Equal(AnnotationVisibility.Build, methodAnn.Annotations.Annotations[0].Visibility);

        // 参数注解（annotation_set_ref_list）
        var paramAnn = Assert.Single(dir.ParameterAnnotations);
        Assert.Single(paramAnn.Parameters.Sets);
        var paramSet = paramAnn.Parameters.Sets[0];
        Assert.NotNull(paramSet);
        var paramItem = Assert.Single(paramSet!.Annotations);
        Assert.Equal(AnnotationVisibility.Runtime, paramItem.Visibility);
    }

    [Fact]
    public void NestedValues_RoundTrip()
    {
        var dex = BuildNestedValuesDex();
        var loaded = DexFile.Load(dex.ToArray());

        var sv = loaded.Classes[0].StaticValues!;
        Assert.Equal(EncodedValueType.Array, sv[0].Type);
        var items = sv[0].AsArray()!;
        Assert.Equal(2, items.Length);
        Assert.Equal(EncodedValueType.Boolean, items[0].Type);
        Assert.True(items[0].AsBoolean());
        Assert.Equal(EncodedValueType.Type, items[1].Type);
        Assert.Equal("Ljava/lang/String;", loaded.GetType(items[1].AsIndex()));

        // 嵌套注解
        var ann = loaded.Classes[0].Annotations!.ClassAnnotations!.Annotations[0];
        var nested = ann.Elements.First(e => loaded.GetString(e.NameIndex) == "inner").Value;
        Assert.Equal(EncodedValueType.Annotation, nested.Type);
        var inner = (AnnotationItem)nested.Value!;
        Assert.Equal(EncodedValueType.Int, inner.Elements[0].Value.Type);
        Assert.Equal(7, inner.Elements[0].Value.AsInt());
    }

    [Fact]
    public void DebugInfo_RawBytesArePreserved()
    {
        var dex = BuildTryCatchDex();
        // 合法 debug_info_item：line_start=1，parameters_size=0，DBG_END_SEQUENCE。
        // A valid debug_info_item: line_start=1, parameters_size=0, DBG_END_SEQUENCE.
        dex.Classes[0].DirectMethods[0].Code!.DebugInfo = [0x01, 0x00, 0x00];
        var loaded = DexFile.Load(dex.ToArray());

        var code = loaded.Classes[0].DirectMethods[0].Code!;
        Assert.NotNull(code.DebugInfo);
        Assert.Equal([0x01, 0x00, 0x00], code.DebugInfo);
    }

    [Fact]
    public void RealMagiskDex_PreservesTriesDebugInfoAndAnnotations()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Firmware", "Files", "magisk_classes2.dex");
        var original = DexFile.Load(path);

        int CountTries(DexFile d) => d.Classes.Sum(c => c.AllMethods.Sum(m => m.Code?.Tries.Count ?? 0));
        int CountDebugInfo(DexFile d) => d.Classes.Sum(c => c.AllMethods.Count(m => m.Code?.DebugInfo != null));
        int CountAnnotations(DexFile d) => d.Classes.Count(c => c.Annotations is { IsEmpty: false });

        var rebuilt = DexFile.Load(original.ToArray());

        Assert.Equal(CountTries(original), CountTries(rebuilt));
        Assert.Equal(CountDebugInfo(original), CountDebugInfo(rebuilt));
        Assert.Equal(CountAnnotations(original), CountAnnotations(rebuilt));
    }

    // ---- 构造辅助 ----

    /// <summary>带 try/catch 的 DEX：run()V，insns = [const/4 v0,#0 ; nop ; return-void]，
    /// try 覆盖 unit 1，handler 捕获 Ljava/lang/Exception; @ unit 2。</summary>
    private static DexFile BuildTryCatchDex()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/TryCatch;",   // 0
            "Ljava/lang/Object;",       // 1
            "Ljava/lang/Exception;",    // 2
            "run",                      // 3
            "()V",                      // 4
            "V"                         // 5
        ]);
        dex.Types.AddRange([
            "Lcom/example/TryCatch;",   // 0
            "Ljava/lang/Object;",       // 1
            "Ljava/lang/Exception;",    // 2
            "V"                         // 3
        ]);
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 5, ReturnTypeIndex = 3 });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 3 });

        var code = new CodeItem
        {
            RegistersSize = 1,
            InsSize = 0,
            OutsSize = 0,
            RawInsns = [0x1200, 0x0000, 0x0E00] // const/4 v0,#0 ; nop ; return-void
        };
        code.Tries.Add(new TryBlock { StartAddress = 1, InsnCount = 1, HandlerIndex = 0 });
        code.Handlers.Add(new CatchHandler());
        code.Handlers[0].Entries.Add(new CatchHandlerEntry { TypeIndex = 2, Address = 2 });

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public,
            Code = code
        });
        dex.Classes.Add(cls);
        return dex;
    }

    /// <summary>纯 catch-all 的 DEX（无类型化条目）。</summary>
    private static DexFile BuildCatchAllDex()
    {
        var dex = BuildTryCatchDex();
        var code = dex.Classes[0].DirectMethods[0].Code!;
        code.Handlers.Clear();
        code.Handlers.Add(new CatchHandler { CatchAllAddress = 2 });
        code.Tries[0].HandlerIndex = 0;
        return dex;
    }

    /// <summary>静态字段初始值：public static final int CONST = 42；public static final String NAME = "hello"。</summary>
    private static DexFile BuildStaticValuesDex()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Consts;",     // 0
            "Ljava/lang/Object;",       // 1
            "CONST",                    // 2
            "NAME",                     // 3
            "hello",                    // 4
            "I",                        // 5
            "Ljava/lang/String;"        // 6
        ]);
        dex.Types.AddRange([
            "Lcom/example/Consts;",     // 0
            "Ljava/lang/Object;",       // 1
            "I",                        // 2
            "Ljava/lang/String;"        // 3
        ]);
        dex.Fields.AddRange([
            new FieldId { ClassIndex = 0, TypeIndex = 2, NameIndex = 2 }, // CONST:I
            new FieldId { ClassIndex = 0, TypeIndex = 3, NameIndex = 3 }  // NAME:String
        ]);

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.StaticFields.Add(new EncodedField { FieldIndex = 0, AccessFlags = AccessFlags.Public | AccessFlags.Static | AccessFlags.Final });
        cls.StaticFields.Add(new EncodedField { FieldIndex = 1, AccessFlags = AccessFlags.Public | AccessFlags.Static | AccessFlags.Final });
        cls.StaticValues =
        [
            EncodedValue.Int(42),
            EncodedValue.String(4)
        ];
        dex.Classes.Add(cls);
        return dex;
    }

    /// <summary>带全范围注解（类/字段/方法/参数）与不同可见性的 DEX。</summary>
    private static DexFile BuildAnnotationsDex()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Ann;",        // 0
            "Lcom/example/Annotated;",  // 1
            "Ljava/lang/Object;",       // 2
            "value",                    // 3
            "class-ann",                // 4
            "field-ann",                // 5
            "method-ann",               // 6
            "param-ann",                // 7
            "CONST",                    // 8
            "doIt",                     // 9
            "I",                        // 10
            "V",                        // 11
            "()V"                       // 12
        ]);
        dex.Types.AddRange([
            "Lcom/example/Ann;",        // 0
            "Lcom/example/Annotated;",  // 1
            "Ljava/lang/Object;",       // 2
            "I",                        // 3
            "V"                         // 4
        ]);
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 11, ReturnTypeIndex = 4 });
        dex.Fields.Add(new FieldId { ClassIndex = 1, TypeIndex = 3, NameIndex = 8 });
        dex.Methods.Add(new MethodId { ClassIndex = 1, ProtoIndex = 0, NameIndex = 9 });

        var cls = new ClassDefinition
        {
            TypeIndex = 1,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 2
        };
        cls.StaticFields.Add(new EncodedField { FieldIndex = 0, AccessFlags = AccessFlags.Public | AccessFlags.Static | AccessFlags.Final });
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public,
            Code = new CodeItem { RegistersSize = 1, InsSize = 0, OutsSize = 0, RawInsns = [0x0E00] }
        });

        // 注解目录
        var dir = new AnnotationsDirectory
        {
            ClassAnnotations = new AnnotationSet()
        };
        dir.ClassAnnotations.Annotations.Add(new AnnotationItem
        {
            TypeIndex = 0,
            Visibility = AnnotationVisibility.Runtime,
            Elements = { new AnnotationElement { NameIndex = 3, Value = EncodedValue.String(4) } }
        });
        dir.FieldAnnotations.Add(new FieldAnnotation
        {
            FieldIndex = 0,
            Annotations = new AnnotationSet()
        });
        dir.FieldAnnotations[0].Annotations.Annotations.Add(new AnnotationItem
        {
            TypeIndex = 0,
            Visibility = AnnotationVisibility.System,
            Elements = { new AnnotationElement { NameIndex = 3, Value = EncodedValue.String(5) } }
        });
        dir.MethodAnnotations.Add(new MethodAnnotation
        {
            MethodIndex = 0,
            Annotations = new AnnotationSet()
        });
        dir.MethodAnnotations[0].Annotations.Annotations.Add(new AnnotationItem
        {
            TypeIndex = 0,
            Visibility = AnnotationVisibility.Build,
            Elements = { new AnnotationElement { NameIndex = 3, Value = EncodedValue.String(6) } }
        });
        dir.ParameterAnnotations.Add(new ParameterAnnotation
        {
            MethodIndex = 0,
            Parameters = new AnnotationSetRefList()
        });
        dir.ParameterAnnotations[0].Parameters.Sets.Add(new AnnotationSet());
        dir.ParameterAnnotations[0].Parameters.Sets[0]!.Annotations.Add(new AnnotationItem
        {
            TypeIndex = 0,
            Visibility = AnnotationVisibility.Runtime,
            Elements = { new AnnotationElement { NameIndex = 3, Value = EncodedValue.String(7) } }
        });
        cls.Annotations = dir;

        dex.Classes.Add(cls);
        return dex;
    }

    /// <summary>嵌套值：静态数组 [true, Ljava/lang/String;] 与含嵌套注解的类注解。</summary>
    private static DexFile BuildNestedValuesDex()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Outer;",      // 0
            "Ljava/lang/Object;",       // 1
            "Ljava/lang/String;",       // 2
            "Lcom/example/Inner;",      // 3
            "FLAGS",                    // 4
            "inner",                    // 5
            "num",                      // 6
            "I"                         // 7
        ]);
        dex.Types.AddRange([
            "Lcom/example/Outer;",      // 0
            "Ljava/lang/Object;",       // 1
            "Ljava/lang/String;",       // 2
            "Lcom/example/Inner;",      // 3
            "I"                         // 4
        ]);
        dex.Fields.Add(new FieldId { ClassIndex = 0, TypeIndex = 4, NameIndex = 4 });

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.StaticFields.Add(new EncodedField { FieldIndex = 0, AccessFlags = AccessFlags.Public | AccessFlags.Static | AccessFlags.Final });
        cls.StaticValues =
        [
            EncodedValue.Array(
                EncodedValue.Boolean(true),
                EncodedValue.TypeRef(2))
        ];

        var dir = new AnnotationsDirectory
        {
            ClassAnnotations = new AnnotationSet()
        };
        dir.ClassAnnotations.Annotations.Add(new AnnotationItem
        {
            TypeIndex = 3,
            Visibility = AnnotationVisibility.Runtime,
            Elements =
            {
                new AnnotationElement
                {
                    NameIndex = 5,
                    Value = EncodedValue.Annotation(new AnnotationItem
                    {
                        TypeIndex = 3,
                        Visibility = AnnotationVisibility.Runtime,
                        Elements = { new AnnotationElement { NameIndex = 6, Value = EncodedValue.Int(7) } }
                    })
                }
            }
        });
        cls.Annotations = dir;
        dex.Classes.Add(cls);
        return dex;
    }
}