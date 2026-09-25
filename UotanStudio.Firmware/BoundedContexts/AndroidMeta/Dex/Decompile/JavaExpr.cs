using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

/// <summary>
/// Base node of the Java expression tree produced by the DEX decompiler; each sealed subclass renders
/// one Java source construct.
/// <para>DEX 反编译器生成的 Java 表达式树基节点；每个 sealed 子类各渲染一种 Java 源码构造。</para>
/// </summary>
public abstract class JavaExpr
{
    /// <summary>
    /// Writes the expression into the string builder.
    /// <para>把表达式写入字符串构建器。</para>
    /// </summary>
    /// <param name="sb">
    /// Builder that receives the rendered text; existing content is kept and never replaced.
    /// <para>接收渲染文本的构建器；已有内容会被保留且不会被替换。</para>
    /// </param>
    public abstract void WriteTo(StringBuilder sb);

    /// <summary>
    /// Renders to a string.
    /// <para>渲染为字符串。</para>
    /// </summary>
    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        WriteTo(sb);
        return sb.ToString();
    }
}

/// <summary>
/// Integer literal expression; <see cref="WriteTo"/> appends the Java <c>L</c> suffix only when the
/// value exceeds the <c>int</c> range.
/// <para>整数字面量表达式；仅当值超出 <c>int</c> 范围时 <see cref="WriteTo"/> 才追加 Java 的 <c>L</c> 后缀。</para>
/// </summary>
public sealed class LiteralExpr : JavaExpr
{
    /// <summary>
    /// The constant value.
    /// <para>常量值。</para>
    /// </summary>
    /// <value>
    /// Always stored as a <see langword="long"/> and rendered in invariant culture.
    /// <para>始终以 <see langword="long"/> 存储，并按固定区域性渲染。</para>
    /// </value>
    public long Value { get; }

    /// <summary>
    /// Creates a numeric literal expression.
    /// <para>创建数值字面量表达式。</para>
    /// </summary>
    /// <param name="value">
    /// The numeric constant to render.
    /// <para>要渲染的数值常量。</para>
    /// </param>
    public LiteralExpr(long value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append(Value.ToString(CultureInfo.InvariantCulture));


        if (Value is < int.MinValue or > int.MaxValue) sb.Append('L');
    }
}

/// <summary>
/// String literal expression rendered as a Java double-quoted string.
/// <para>以 Java 双引号字符串形式渲染的字符串字面量表达式。</para>
/// </summary>
public sealed class StringExpr : JavaExpr
{
    /// <summary>
    /// Raw string content without surrounding quotes.
    /// <para>不含两侧引号的原始字符串内容。</para>
    /// </summary>
    /// <value>
    /// At render time <c>"</c>, <c>\</c>, newline, carriage return and tab are escaped; every other
    /// character is emitted as-is.
    /// <para>渲染时会对 <c>"</c>、<c>\</c>、换行、回车和制表符进行转义；其余字符原样输出。</para>
    /// </value>
    public string Value { get; }

    /// <summary>
    /// Creates a string literal expression.
    /// <para>创建字符串字面量表达式。</para>
    /// </summary>
    /// <param name="value">
    /// Raw content; quoting and escaping are applied when the expression is written.
    /// <para>原始内容；引号与转义在写入表达式时施加。</para>
    /// </param>
    public StringExpr(string value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in Value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>
/// Class literal expression rendered as <c>TypeName.class</c>.
/// <para>以 <c>TypeName.class</c> 形式渲染的类字面量表达式。</para>
/// </summary>
public sealed class ClassLiteralExpr : JavaExpr
{
    /// <summary>
    /// Java-style type name emitted verbatim before <c>.class</c>.
    /// <para>直接输出在 <c>.class</c> 之前的 Java 风格类型名。</para>
    /// </summary>
    /// <value>
    /// The node performs no type lookup; the text is only what the caller stored here.
    /// <para>本节点不做类型查找；文本仅是调用方存入的内容。</para>
    /// </value>
    public string TypeName { get; }

    /// <summary>
    /// Creates a class literal expression.
    /// <para>创建类字面量表达式。</para>
    /// </summary>
    /// <param name="typeName">
    /// Type name to place before the <c>.class</c> suffix.
    /// <para>要放在 <c>.class</c> 后缀之前的类型名。</para>
    /// </param>
    public ClassLiteralExpr(string typeName) => TypeName = typeName;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append(TypeName).Append(".class");
}

/// <summary>
/// The Java <c>null</c> literal, exposed through one shared instance.
/// <para>Java 的 <c>null</c> 字面量，通过单一共享实例暴露。</para>
/// </summary>
public sealed class NullExpr : JavaExpr
{
    /// <summary>
    /// Shared instance that renders as <c>null</c>.
    /// <para>渲染为 <c>null</c> 的共享实例。</para>
    /// </summary>
    /// <value>
    /// Always the same immutable object; construction is closed.
    /// <para>始终是同一个不可变对象；不对外开放构造。</para>
    /// </value>
    public static NullExpr Instance { get; } = new();

    private NullExpr() { }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append("null");
}

/// <summary>
/// Reference to a local variable or method register, rendered as its bare name.
/// <para>对局部变量或方法寄存器的引用，按裸名称渲染。</para>
/// </summary>
public sealed class LocalExpr : JavaExpr
{
    /// <summary>
    /// Identifier text of the register.
    /// <para>寄存器的标识符文本。</para>
    /// </summary>
    /// <value>
    /// Appended unchanged by <see cref="WriteTo"/>.
    /// <para>由 <see cref="WriteTo"/> 原样追加。</para>
    /// </value>
    public string Name { get; }

    /// <summary>
    /// Declared type of the register, kept as consumer metadata.
    /// <para>寄存器的声明类型，作为供使用者读取的元数据保留。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> when the type is unknown; never rendered by <see cref="WriteTo"/>.
    /// <para>类型未知时为 <see langword="null"/>；<see cref="WriteTo"/> 从不渲染它。</para>
    /// </value>
    public string? Type { get; }

    /// <summary>
    /// Creates a local register expression.
    /// <para>创建局部寄存器表达式。</para>
    /// </summary>
    /// <param name="name">
    /// Identifier text to render.
    /// <para>要渲染的标识符文本。</para>
    /// </param>
    /// <param name="type">
    /// Declared type, or <see langword="null"/> when the decompiler has not resolved one.
    /// <para>声明类型；反编译器尚未解析出类型时为 <see langword="null"/>。</para>
    /// </param>
    public LocalExpr(string name, string? type = null)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append(Name);
}

/// <summary>
/// Field access expression; rendering picks the static owner form first, then the target form, then
/// the bare name.
/// <para>字段访问表达式；渲染时依次优先采用静态所属类形式、目标对象形式，最后是裸名称。</para>
/// </summary>
public sealed class FieldExpr : JavaExpr
{
    /// <summary>
    /// Receiver expression for an instance field.
    /// <para>实例字段的接收者表达式。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> for static fields or an implicit <c>this</c>; ignored when
    /// <see cref="IsStatic"/> is set.
    /// <para>静态字段或隐式 <c>this</c> 时为 <see langword="null"/>；<see cref="IsStatic"/> 置位时被忽略。</para>
    /// </value>
    public JavaExpr? Target { get; }

    /// <summary>
    /// Class that declares the field.
    /// <para>声明该字段的类。</para>
    /// </summary>
    /// <value>
    /// Only rendered in the static form <c>OwnerClass.Name</c>.
    /// <para>仅在静态形式 <c>OwnerClass.Name</c> 中渲染。</para>
    /// </value>
    public string OwnerClass { get; }

    /// <summary>
    /// Field name.
    /// <para>字段名。</para>
    /// </summary>
    /// <value>
    /// Always rendered, either after the owner class, after the target, or bare.
    /// <para>始终渲染，分别位于所属类之后、目标表达式之后，或以裸名称出现。</para>
    /// </value>
    public string Name { get; }

    /// <summary>
    /// Declared type of the field.
    /// <para>字段的声明类型。</para>
    /// </summary>
    /// <value>
    /// Consumer metadata carried alongside the access; never rendered.
    /// <para>随访问表达式一并携带、供使用者读取的元数据；从不渲染。</para>
    /// </value>
    public string Type { get; }

    /// <summary>
    /// Whether the field is static.
    /// <para>该字段是否为静态字段。</para>
    /// </summary>
    /// <value>
    /// When <see langword="true"/> the owner class prefix replaces the receiver in the output.
    /// <para>为 <see langword="true"/> 时，输出中以所属类前缀替代接收者。</para>
    /// </value>
    public bool IsStatic { get; }

    /// <summary>
    /// Creates a field access expression.
    /// <para>创建字段访问表达式。</para>
    /// </summary>
    /// <param name="target">
    /// Receiver expression, or <see langword="null"/> for static or implicit-<c>this</c> access.
    /// <para>接收者表达式；静态访问或隐式 <c>this</c> 访问时为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="ownerClass">
    /// Declaring class name used by the static rendering form.
    /// <para>静态渲染形式所使用的声明类名。</para>
    /// </param>
    /// <param name="name">
    /// Field name to render.
    /// <para>要渲染的字段名。</para>
    /// </param>
    /// <param name="type">
    /// Declared field type, kept as metadata.
    /// <para>字段声明类型，作为元数据保留。</para>
    /// </param>
    /// <param name="isStatic">
    /// Whether the field is accessed statically.
    /// <para>该字段是否以静态方式访问。</para>
    /// </param>
    public FieldExpr(JavaExpr? target, string ownerClass, string name, string type, bool isStatic)
    {
        Target = target;
        OwnerClass = ownerClass;
        Name = name;
        Type = type;
        IsStatic = isStatic;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        if (IsStatic)
        {
            sb.Append(OwnerClass).Append('.').Append(Name);
        }
        else if (Target is not null)
        {
            Target.WriteTo(sb);
            sb.Append('.').Append(Name);
        }
        else
        {
            sb.Append(Name);
        }
    }
}

/// <summary>
/// Method invocation expression; the callee renders as <c>super.Name</c>, <c>OwnerClass.Name</c>,
/// <c>target.Name</c> or a bare name in that precedence, followed by comma-separated arguments.
/// <para>方法调用表达式；被调用者按上述优先级渲染为 <c>super.Name</c>、<c>OwnerClass.Name</c>、<c>target.Name</c> 或裸名称，随后以逗号分隔渲染实参。</para>
/// </summary>
public sealed class MethodCallExpr : JavaExpr
{
    /// <summary>
    /// Receiver expression for an instance call.
    /// <para>实例调用的接收者表达式。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> for static, super or implicit-<c>this</c> calls; used only when neither
    /// <see cref="IsSuperCall"/> nor <see cref="IsStatic"/> is set.
    /// <para>静态、super 或隐式 <c>this</c> 调用时为 <see langword="null"/>；仅在 <see cref="IsSuperCall"/> 与 <see cref="IsStatic"/> 均未置位时使用。</para>
    /// </value>
    public JavaExpr? Target { get; }

    /// <summary>
    /// Class that declares the method.
    /// <para>声明该方法的类。</para>
    /// </summary>
    /// <value>
    /// Only rendered in the static form <c>OwnerClass.Name</c>.
    /// <para>仅在静态形式 <c>OwnerClass.Name</c> 中渲染。</para>
    /// </value>
    public string OwnerClass { get; }

    /// <summary>
    /// Method name.
    /// <para>方法名。</para>
    /// </summary>
    /// <value>
    /// Always rendered as part of the callee.
    /// <para>始终作为被调用者的一部分渲染。</para>
    /// </value>
    public string Name { get; }

    /// <summary>
    /// Declared return type of the method.
    /// <para>方法的声明返回类型。</para>
    /// </summary>
    /// <value>
    /// Consumer metadata carried alongside the call; never rendered.
    /// <para>随调用表达式一并携带、供使用者读取的元数据；从不渲染。</para>
    /// </value>
    public string ReturnType { get; }

    /// <summary>
    /// Actual argument expressions in declaration order.
    /// <para>按声明顺序排列的实参表达式。</para>
    /// </summary>
    /// <value>
    /// May be empty, which renders <c>()</c>; each element is written through its own
    /// <see cref="JavaExpr.WriteTo"/>.
    /// <para>可以为空，此时渲染为 <c>()</c>；每个元素通过自身的 <see cref="JavaExpr.WriteTo"/> 写出。</para>
    /// </value>
    public IReadOnlyList<JavaExpr> Args { get; }

    /// <summary>
    /// Whether the method is static.
    /// <para>该方法是否为静态方法。</para>
    /// </summary>
    /// <value>
    /// When <see langword="true"/> the owner class prefix replaces the receiver, unless
    /// <see cref="IsSuperCall"/> takes precedence.
    /// <para>为 <see langword="true"/> 时以所属类前缀替代接收者，除非 <see cref="IsSuperCall"/> 具有更高优先级。</para>
    /// </value>
    public bool IsStatic { get; }

    /// <summary>
    /// Whether the call delegates to the superclass implementation.
    /// <para>该调用是否委派给父类实现。</para>
    /// </summary>
    /// <value>
    /// When <see langword="true"/> the callee is rendered as <c>super.Name</c> regardless of the
    /// other qualifier flags.
    /// <para>为 <see langword="true"/> 时，无论其他限定标志如何，被调用者都渲染为 <c>super.Name</c>。</para>
    /// </value>
    public bool IsSuperCall { get; }

    /// <summary>
    /// Creates a method call expression.
    /// <para>创建方法调用表达式。</para>
    /// </summary>
    /// <param name="target">
    /// Receiver expression, or <see langword="null"/> when the call has no explicit receiver.
    /// <para>接收者表达式；调用没有显式接收者时为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="ownerClass">
    /// Declaring class name used by the static rendering form.
    /// <para>静态渲染形式所使用的声明类名。</para>
    /// </param>
    /// <param name="name">
    /// Method name to render.
    /// <para>要渲染的方法名。</para>
    /// </param>
    /// <param name="returnType">
    /// Declared return type, kept as metadata.
    /// <para>声明返回类型，作为元数据保留。</para>
    /// </param>
    /// <param name="args">
    /// Argument expressions rendered inside the parentheses in this order.
    /// <para>按此顺序渲染在括号内的实参表达式。</para>
    /// </param>
    /// <param name="isStatic">
    /// Whether the method is invoked statically.
    /// <para>该方法是否以静态方式调用。</para>
    /// </param>
    /// <param name="isSuperCall">
    /// Whether the callee is qualified with <c>super.</c>; defaults to <see langword="false"/>.
    /// <para>被调用者是否以 <c>super.</c> 限定；默认为 <see langword="false"/>。</para>
    /// </param>
    public MethodCallExpr(JavaExpr? target, string ownerClass, string name, string returnType, IReadOnlyList<JavaExpr> args, bool isStatic, bool isSuperCall = false)
    {
        Target = target;
        OwnerClass = ownerClass;
        Name = name;
        ReturnType = returnType;
        Args = args;
        IsStatic = isStatic;
        IsSuperCall = isSuperCall;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        if (IsSuperCall)
        {
            sb.Append("super.").Append(Name);
        }
        else if (IsStatic)
        {
            sb.Append(OwnerClass).Append('.').Append(Name);
        }
        else if (Target is not null)
        {
            Target.WriteTo(sb);
            sb.Append('.').Append(Name);
        }
        else
        {
            sb.Append(Name);
        }
        sb.Append('(');
        for (var i = 0; i < Args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Args[i].WriteTo(sb);
        }
        sb.Append(')');
    }
}

/// <summary>
/// Object construction expression rendered as <c>new Type(...)</c>.
/// <para>以 <c>new Type(...)</c> 形式渲染的对象构造表达式。</para>
/// </summary>
public sealed class NewInstanceExpr : JavaExpr
{
    /// <summary>
    /// Type being constructed.
    /// <para>正在构造的类型。</para>
    /// </summary>
    /// <value>
    /// Rendered verbatim after the <c>new </c> keyword.
    /// <para>原样渲染在 <c>new </c> 关键字之后。</para>
    /// </value>
    public string Type { get; }

    /// <summary>
    /// Constructor arguments rendered inside the parentheses.
    /// <para>渲染在括号内的构造器实参。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> renders an empty parameter list <c>()</c>; the property is writable, so
    /// arguments may be attached after construction.
    /// <para>为 <see langword="null"/> 时渲染为空参数列表 <c>()</c>；该属性可写，因此实参允许在构造之后附加。</para>
    /// </value>
    public IReadOnlyList<JavaExpr>? Args { get; set; }

    /// <summary>
    /// Creates an object construction expression.
    /// <para>创建对象构造表达式。</para>
    /// </summary>
    /// <param name="type">
    /// Type name to construct; arguments are attached later through <see cref="Args"/>.
    /// <para>要构造的类型名；实参稍后经由 <see cref="Args"/> 附加。</para>
    /// </param>
    public NewInstanceExpr(string type) => Type = type;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append("new ").Append(Type).Append('(');
        if (Args is not null)
        {
            for (var i = 0; i < Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Args[i].WriteTo(sb);
            }
        }
        sb.Append(')');
    }
}

/// <summary>
/// Delegating constructor call rendered as <c>this(...)</c> or <c>super(...)</c>.
/// <para>以 <c>this(...)</c> 或 <c>super(...)</c> 形式渲染的构造器委托调用。</para>
/// </summary>
public sealed class ConstructorCallExpr : JavaExpr
{
    /// <summary>
    /// Which sibling constructor the call delegates to.
    /// <para>该调用委托给哪一个同族构造器。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> renders the <c>this(</c> prefix, <see langword="false"/> renders
    /// <c>super(</c>.
    /// <para>为 <see langword="true"/> 时渲染 <c>this(</c> 前缀，为 <see langword="false"/> 时渲染 <c>super(</c>。</para>
    /// </value>
    public bool IsThisCall { get; }

    /// <summary>
    /// Arguments rendered inside the parentheses in declaration order.
    /// <para>按声明顺序渲染在括号内的实参。</para>
    /// </summary>
    /// <value>
    /// May be empty, which renders <c>()</c> or <c>super()</c> style calls.
    /// <para>可以为空，此时渲染为 <c>()</c> 或 <c>super()</c> 形式的调用。</para>
    /// </value>
    public IReadOnlyList<JavaExpr> Args { get; }

    /// <summary>
    /// Creates a constructor delegation call expression.
    /// <para>创建构造器委托调用表达式。</para>
    /// </summary>
    /// <param name="isThisCall">
    /// <see langword="true"/> for a <c>this(...)</c> delegation, <see langword="false"/> for
    /// <c>super(...)</c>.
    /// <para><see langword="true"/> 表示 <c>this(...)</c> 委托，<see langword="false"/> 表示 <c>super(...)</c>。</para>
    /// </param>
    /// <param name="args">
    /// Argument expressions to render between the parentheses.
    /// <para>要渲染在括号之间的实参表达式。</para>
    /// </param>
    public ConstructorCallExpr(bool isThisCall, IReadOnlyList<JavaExpr> args)
    {
        IsThisCall = isThisCall;
        Args = args;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append(IsThisCall ? "this(" : "super(");
        for (var i = 0; i < Args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Args[i].WriteTo(sb);
        }
        sb.Append(')');
    }
}

/// <summary>
/// Array construction expression; a non-empty initializer list takes rendering precedence over the
/// size expression.
/// <para>数组构造表达式；非空初始化列表在渲染时优先于长度表达式。</para>
/// </summary>
public sealed class NewArrayExpr : JavaExpr
{
    /// <summary>
    /// Element type of the array.
    /// <para>数组的元素类型。</para>
    /// </summary>
    /// <value>
    /// Rendered verbatim after the <c>new </c> keyword.
    /// <para>原样渲染在 <c>new </c> 关键字之后。</para>
    /// </value>
    public string ElementType { get; }

    /// <summary>
    /// Size expression for a size-only array construction.
    /// <para>仅按长度构造数组时的长度表达式。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> renders empty brackets <c>[]</c>; ignored whenever
    /// <see cref="Initializers"/> is non-empty.
    /// <para>为 <see langword="null"/> 时渲染为空括号 <c>[]</c>；只要 <see cref="Initializers"/> 非空就被忽略。</para>
    /// </value>
    public JavaExpr? Size { get; }

    /// <summary>
    /// Element initializers for an array filled with literal content.
    /// <para>以字面量内容填充数组时的元素初始化器。</para>
    /// </summary>
    /// <value>
    /// Mutable list, empty by default; when non-empty, rendering emits
    /// <c>new T[n] { ... }</c> with <c>n</c> taken from the list count and skips <see cref="Size"/>.
    /// <para>可变更列表，默认为空；非空时渲染为 <c>new T[n] { ... }</c>，其中 <c>n</c> 取自列表数量，并忽略 <see cref="Size"/>。</para>
    /// </value>
    public List<JavaExpr> Initializers { get; } = [];

    /// <summary>
    /// Creates an array construction expression.
    /// <para>创建数组构造表达式。</para>
    /// </summary>
    /// <param name="elementType">
    /// Element type name to render.
    /// <para>要渲染的元素类型名。</para>
    /// </param>
    /// <param name="size">
    /// Size expression, or <see langword="null"/> for empty brackets or an initializer-filled array.
    /// <para>长度表达式；渲染空括号或按初始化列表填充的数组时为 <see langword="null"/>。</para>
    /// </param>
    public NewArrayExpr(string elementType, JavaExpr? size)
    {
        ElementType = elementType;
        Size = size;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append("new ").Append(ElementType);
        if (Initializers.Count > 0)
        {
            sb.Append('[').Append(Initializers.Count).Append(']');
            sb.Append(" { ");
            for (var i = 0; i < Initializers.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Initializers[i].WriteTo(sb);
            }
            sb.Append(" }");
        }
        else
        {
            sb.Append('[');
            Size?.WriteTo(sb);
            sb.Append(']');
        }
    }
}

/// <summary>
/// Explicit cast expression rendered as <c>(Type) inner</c>.
/// <para>以 <c>(Type) inner</c> 形式渲染的显式转型表达式。</para>
/// </summary>
public sealed class CastExpr : JavaExpr
{
    /// <summary>
    /// Target type of the cast.
    /// <para>转型的目标类型。</para>
    /// </summary>
    /// <value>
    /// Rendered verbatim inside the leading parentheses.
    /// <para>原样渲染在前置括号内。</para>
    /// </value>
    public string Type { get; }

    /// <summary>
    /// Operand being cast.
    /// <para>被转型的操作数。</para>
    /// </summary>
    /// <value>
    /// Written after the cast marker using its own rendering rules.
    /// <para>在转型标记之后按其自身的渲染规则写出。</para>
    /// </value>
    public JavaExpr Inner { get; }

    /// <summary>
    /// Creates an explicit cast expression.
    /// <para>创建显式转型表达式。</para>
    /// </summary>
    /// <param name="type">
    /// Cast target type name.
    /// <para>转型目标类型名。</para>
    /// </param>
    /// <param name="inner">
    /// Expression to cast.
    /// <para>待转型的表达式。</para>
    /// </param>
    public CastExpr(string type, JavaExpr inner)
    {
        Type = type;
        Inner = inner;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append('(').Append(Type).Append(") ");
        Inner.WriteTo(sb);
    }
}

/// <summary>
/// Binary operation always wrapped in parentheses: <c>(left op right)</c>.
/// <para>始终外加括号的二元运算：<c>(left op right)</c>。</para>
/// </summary>
public sealed class BinaryExpr : JavaExpr
{
    /// <summary>
    /// Operator text placed between the operands.
    /// <para>置于两个操作数之间的运算符文本。</para>
    /// </summary>
    /// <value>
    /// Rendered verbatim with one space on each side.
    /// <para>原样渲染，两侧各有一个空格。</para>
    /// </value>
    public string Op { get; }

    /// <summary>
    /// Left operand.
    /// <para>左操作数。</para>
    /// </summary>
    /// <value>
    /// Written first inside the parentheses using its own rendering rules.
    /// <para>最先写在外层括号内，按其自身的渲染规则呈现。</para>
    /// </value>
    public JavaExpr Left { get; }

    /// <summary>
    /// Right operand.
    /// <para>右操作数。</para>
    /// </summary>
    /// <value>
    /// Written after the operator using its own rendering rules.
    /// <para>写在运算符之后，按其自身的渲染规则呈现。</para>
    /// </value>
    public JavaExpr Right { get; }

    /// <summary>
    /// Creates a binary operation expression.
    /// <para>创建二元运算表达式。</para>
    /// </summary>
    /// <param name="op">
    /// Operator token, for example <c>+</c> or <c>&amp;&amp;</c>.
    /// <para>运算符记号，例如 <c>+</c> 或 <c>&amp;&amp;</c>。</para>
    /// </param>
    /// <param name="left">
    /// Left operand expression.
    /// <para>左操作数表达式。</para>
    /// </param>
    /// <param name="right">
    /// Right operand expression.
    /// <para>右操作数表达式。</para>
    /// </param>
    public BinaryExpr(string op, JavaExpr left, JavaExpr right)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append('(');
        Left.WriteTo(sb);
        sb.Append(' ').Append(Op).Append(' ');
        Right.WriteTo(sb);
        sb.Append(')');
    }
}

/// <summary>
/// Unary operation rendered as <c>op</c> followed by its operand; a nested
/// <see cref="BinaryExpr"/> operand gains extra parentheses.
/// <para>以 <c>op</c> 紧跟操作数渲染的一元运算；操作数为嵌套 <see cref="BinaryExpr"/> 时会额外加括号。</para>
/// </summary>
public sealed class UnaryExpr : JavaExpr
{
    /// <summary>
    /// Operator text placed before the operand.
    /// <para>置于操作数之前的运算符文本。</para>
    /// </summary>
    /// <value>
    /// Appended verbatim with no separating space.
    /// <para>原样追加且不加空格。</para>
    /// </value>
    public string Op { get; }

    /// <summary>
    /// Operand the operator applies to.
    /// <para>运算符所作用的操作数。</para>
    /// </summary>
    /// <value>
    /// Written after the operator; parenthesized only when it is itself a
    /// <see cref="BinaryExpr"/>.
    /// <para>写在运算符之后；仅当其本身是 <see cref="BinaryExpr"/> 时才加括号。</para>
    /// </value>
    public JavaExpr Inner { get; }

    /// <summary>
    /// Creates a unary operation expression.
    /// <para>创建一元运算表达式。</para>
    /// </summary>
    /// <param name="op">
    /// Operator token, for example <c>-</c> or <c>!</c>.
    /// <para>运算符记号，例如 <c>-</c> 或 <c>!</c>。</para>
    /// </param>
    /// <param name="inner">
    /// Operand expression.
    /// <para>操作数表达式。</para>
    /// </param>
    public UnaryExpr(string op, JavaExpr inner)
    {
        Op = op;
        Inner = inner;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append(Op);


        if (Inner is BinaryExpr)
        {
            sb.Append('(');
            Inner.WriteTo(sb);
            sb.Append(')');
        }
        else
        {
            Inner.WriteTo(sb);
        }
    }
}

/// <summary>
/// Array element access rendered as <c>array[index]</c>.
/// <para>以 <c>array[index]</c> 形式渲染的数组元素访问。</para>
/// </summary>
public sealed class ArrayAccessExpr : JavaExpr
{
    /// <summary>
    /// Element type of the accessed array.
    /// <para>被访问数组的元素类型。</para>
    /// </summary>
    /// <value>
    /// Consumer metadata carried alongside the access; never rendered.
    /// <para>随访问表达式一并携带、供使用者读取的元数据；从不渲染。</para>
    /// </value>
    public string Type { get; }

    /// <summary>
    /// Expression producing the array being indexed.
    /// <para>产生被索引数组的表达式。</para>
    /// </summary>
    /// <value>
    /// Written before the brackets using its own rendering rules.
    /// <para>写在方括号之前，按其自身的渲染规则呈现。</para>
    /// </value>
    public JavaExpr Array { get; }

    /// <summary>
    /// Index expression.
    /// <para>下标表达式。</para>
    /// </summary>
    /// <value>
    /// Written inside the brackets using its own rendering rules.
    /// <para>写在方括号之内，按其自身的渲染规则呈现。</para>
    /// </value>
    public JavaExpr Index { get; }

    /// <summary>
    /// Creates an array index access expression.
    /// <para>创建数组下标访问表达式。</para>
    /// </summary>
    /// <param name="type">
    /// Element type, kept as metadata.
    /// <para>元素类型，作为元数据保留。</para>
    /// </param>
    /// <param name="array">
    /// Array expression to index.
    /// <para>待索引的数组表达式。</para>
    /// </param>
    /// <param name="index">
    /// Index expression placed between the brackets.
    /// <para>放在方括号之间的下标表达式。</para>
    /// </param>
    public ArrayAccessExpr(string type, JavaExpr array, JavaExpr index)
    {
        Type = type;
        Array = array;
        Index = index;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        Array.WriteTo(sb);
        sb.Append('[');
        Index.WriteTo(sb);
        sb.Append(']');
    }
}

/// <summary>
/// Runtime type test rendered as <c>inner instanceof Type</c>.
/// <para>以 <c>inner instanceof Type</c> 形式渲染的运行时类型测试。</para>
/// </summary>
public sealed class InstanceOfExpr : JavaExpr
{
    /// <summary>
    /// Expression whose runtime type is tested.
    /// <para>待测试运行时类型的表达式。</para>
    /// </summary>
    /// <value>
    /// Written before the <c> instanceof </c> keyword using its own rendering rules.
    /// <para>写在 <c> instanceof </c> 关键字之前，按其自身的渲染规则呈现。</para>
    /// </value>
    public JavaExpr Inner { get; }

    /// <summary>
    /// Type the operand is tested against.
    /// <para>操作数所要测试的目标类型。</para>
    /// </summary>
    /// <value>
    /// Rendered verbatim after the keyword.
    /// <para>原样渲染在关键字之后。</para>
    /// </value>
    public string Type { get; }

    /// <summary>
    /// Creates an instanceof expression.
    /// <para>创建 instanceof 表达式。</para>
    /// </summary>
    /// <param name="inner">
    /// Expression to test.
    /// <para>待测试的表达式。</para>
    /// </param>
    /// <param name="type">
    /// Type name placed after <c>instanceof</c>.
    /// <para>放在 <c>instanceof</c> 之后的类型名。</para>
    /// </param>
    public InstanceOfExpr(JavaExpr inner, string type)
    {
        Inner = inner;
        Type = type;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        Inner.WriteTo(sb);
        sb.Append(" instanceof ").Append(Type);
    }
}

/// <summary>
/// Conditional expression always wrapped in parentheses: <c>(cond ? then : else)</c>.
/// <para>始终外加括号的三元条件表达式：<c>(cond ? then : else)</c>。</para>
/// </summary>
public sealed class TernaryExpr : JavaExpr
{
    /// <summary>
    /// Boolean condition evaluated first.
    /// <para>最先求值的布尔条件。</para>
    /// </summary>
    /// <value>
    /// Written before the <c> ? </c> separator using its own rendering rules.
    /// <para>写在 <c> ? </c> 分隔符之前，按其自身的渲染规则呈现。</para>
    /// </value>
    public JavaExpr Cond { get; }

    /// <summary>
    /// Branch taken when the condition holds.
    /// <para>条件成立时采用的分支。</para>
    /// </summary>
    /// <value>
    /// Written between <c> ? </c> and <c> : </c>.
    /// <para>写在 <c> ? </c> 与 <c> : </c> 之间。</para>
    /// </value>
    public JavaExpr Then { get; }

    /// <summary>
    /// Branch taken when the condition does not hold.
    /// <para>条件不成立时采用的分支。</para>
    /// </summary>
    /// <value>
    /// Written after the <c> : </c> separator.
    /// <para>写在 <c> : </c> 分隔符之后。</para>
    /// </value>
    public JavaExpr Else { get; }

    /// <summary>
    /// Creates a ternary conditional expression.
    /// <para>创建三元条件表达式。</para>
    /// </summary>
    /// <param name="cond">
    /// Condition expression.
    /// <para>条件表达式。</para>
    /// </param>
    /// <param name="then">
    /// Expression rendered when the condition holds.
    /// <para>条件成立时渲染的表达式。</para>
    /// </param>
    /// <param name="els">
    /// Expression rendered when the condition does not hold.
    /// <para>条件不成立时渲染的表达式。</para>
    /// </param>
    public TernaryExpr(JavaExpr cond, JavaExpr then, JavaExpr els)
    {
        Cond = cond;
        Then = then;
        Else = els;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append('(');
        Cond.WriteTo(sb);
        sb.Append(" ? ");
        Then.WriteTo(sb);
        sb.Append(" : ");
        Else.WriteTo(sb);
        sb.Append(')');
    }
}

/// <summary>
/// Fallback expression for a construct the decompiler did not recognise.
/// <para>反编译器未能识别的构造所使用的兜底表达式。</para>
/// </summary>
public sealed class UnknownExpr : JavaExpr
{
    /// <summary>
    /// Placeholder text describing the unrecognised construct.
    /// <para>描述未识别构造的占位文本。</para>
    /// </summary>
    /// <value>
    /// Appended verbatim by <see cref="WriteTo"/> with no escaping or validation; the text is
    /// produced by the decompiler for constructs it cannot render.
    /// <para>由 <see cref="WriteTo"/> 原样追加，不做转义或校验；该文本由反编译器为其无法渲染的构造产生。</para>
    /// </value>
    public string Hint { get; }

    /// <summary>
    /// Creates an unknown expression.
    /// <para>创建未知表达式。</para>
    /// </summary>
    /// <param name="hint">
    /// Text to emit verbatim in place of a rendered expression.
    /// <para>用于替代已渲染表达式、原样输出的文本。</para>
    /// </param>
    public UnknownExpr(string hint) => Hint = hint;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append(Hint);
}