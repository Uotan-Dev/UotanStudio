using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>伪 Java 表达式基类：由寄存器模拟产生的表达式树节点，负责渲染为 Java 风格的文本。</para>
/// Base class of pseudo-Java expressions: expression tree nodes produced by the register simulation,
/// each rendering itself as Java-style text.
/// </summary>
public abstract class JavaExpr
{
    /// <summary>
    /// <para>把表达式写入字符串构建器。</para>
    /// Writes the expression into the string builder.
    /// </summary>
    /// <param name="sb">目标构建器。<para>Target builder.</para></param>
    public abstract void WriteTo(StringBuilder sb);

    /// <summary>
    /// <para>渲染为字符串。</para>
    /// Renders to a string.
    /// </summary>
    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        WriteTo(sb);
        return sb.ToString();
    }
}

/// <summary>
/// <para>数值字面量表达式。</para>
/// Numeric literal expression.
/// </summary>
public sealed class LiteralExpr : JavaExpr
{
    /// <summary>字面量值。<para>Literal value.</para></summary>
    public long Value { get; }

    /// <summary>
    /// <para>创建数值字面量表达式。</para>
    /// Creates a numeric literal expression.
    /// </summary>
    /// <param name="value">字面量值。<para>Literal value.</para></param>
    public LiteralExpr(long value) => Value = value;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append(Value.ToString(CultureInfo.InvariantCulture));
        // 超出 int 范围时追加 L 后缀，保持 Java 语义。
        // Append the L suffix beyond the int range to keep Java semantics.
        if (Value is < int.MinValue or > int.MaxValue) sb.Append('L');
    }
}

/// <summary>
/// <para>字符串字面量表达式。</para>
/// String literal expression.
/// </summary>
public sealed class StringExpr : JavaExpr
{
    /// <summary>字符串内容（未转义）。<para>String content (unescaped).</para></summary>
    public string Value { get; }

    /// <summary>
    /// <para>创建字符串字面量表达式。</para>
    /// Creates a string literal expression.
    /// </summary>
    /// <param name="value">字符串内容。<para>String content.</para></param>
    public StringExpr(string value) => Value = value;

    /// <inheritdoc />
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
/// <para>类字面量表达式（const-class 产生的 <c>X.class</c>）。</para>
/// Class literal expression (<c>X.class</c> from const-class).
/// </summary>
public sealed class ClassLiteralExpr : JavaExpr
{
    /// <summary>类名（可读形式）。<para>Class name (readable form).</para></summary>
    public string TypeName { get; }

    /// <summary>
    /// <para>创建类字面量表达式。</para>
    /// Creates a class literal expression.
    /// </summary>
    /// <param name="typeName">类名。<para>Class name.</para></param>
    public ClassLiteralExpr(string typeName) => TypeName = typeName;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb) => sb.Append(TypeName).Append(".class");
}

/// <summary>
/// <para>null 字面量表达式。</para>
/// Null literal expression.
/// </summary>
public sealed class NullExpr : JavaExpr
{
    /// <summary>
    /// <para>获取共享的 null 表达式实例。</para>
    /// Gets the shared null expression instance.
    /// </summary>
    public static NullExpr Instance { get; } = new();

    private NullExpr() { }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb) => sb.Append("null");
}

/// <summary>
/// <para>局部寄存器表达式（如 <c>v0</c>、<c>p1</c>）。</para>
/// Local register expression (e.g. <c>v0</c>, <c>p1</c>).
/// </summary>
public sealed class LocalExpr : JavaExpr
{
    /// <summary>寄存器名称。<para>Register name.</para></summary>
    public string Name { get; }

    /// <summary>
    /// <para>静态类型提示（可读形式，如 "int"、"java.lang.String"）；未知为 <c>null</c>。</para>
    /// Static type hint (readable form, e.g. "int", "java.lang.String"); <c>null</c> when unknown.
    /// </summary>
    public string? Type { get; }

    /// <summary>
    /// <para>创建局部寄存器表达式。</para>
    /// Creates a local register expression.
    /// </summary>
    /// <param name="name">寄存器名称。<para>Register name.</para></param>
    /// <param name="type">类型提示。<para>Type hint.</para></param>
    public LocalExpr(string name, string? type = null)
    {
        Name = name;
        Type = type;
    }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb) => sb.Append(Name);
}

/// <summary>
/// <para>字段访问表达式（iget/sget 系列）。</para>
/// Field access expression (iget/sget family).
/// </summary>
public sealed class FieldExpr : JavaExpr
{
    /// <summary>实例目标；静态字段为 <c>null</c>。<para>Instance target; <c>null</c> for static fields.</para></summary>
    public JavaExpr? Target { get; }

    /// <summary>所属类名（可读形式），静态字段渲染用。<para>Owning class name (readable form), used when rendering static fields.</para></summary>
    public string OwnerClass { get; }

    /// <summary>字段名。<para>Field name.</para></summary>
    public string Name { get; }

    /// <summary>字段类型（可读形式）。<para>Field type (readable form).</para></summary>
    public string Type { get; }

    /// <summary>是否为静态字段。<para>Whether the field is static.</para></summary>
    public bool IsStatic { get; }

    /// <summary>
    /// <para>创建字段访问表达式。</para>
    /// Creates a field access expression.
    /// </summary>
    /// <param name="target">实例目标（静态时为 <c>null</c>）。<para>Instance target (<c>null</c> when static).</para></param>
    /// <param name="ownerClass">所属类名。<para>Owning class name.</para></param>
    /// <param name="name">字段名。<para>Field name.</para></param>
    /// <param name="type">字段类型。<para>Field type.</para></param>
    /// <param name="isStatic">是否静态。<para>Whether static.</para></param>
    public FieldExpr(JavaExpr? target, string ownerClass, string name, string type, bool isStatic)
    {
        Target = target;
        OwnerClass = ownerClass;
        Name = name;
        Type = type;
        IsStatic = isStatic;
    }

    /// <inheritdoc />
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
/// <para>方法调用表达式（invoke 系列，不含构造器，构造器并入 <see cref="NewInstanceExpr"/>）。</para>
/// Method call expression (invoke family; constructors are folded into <see cref="NewInstanceExpr"/>).
/// </summary>
public sealed class MethodCallExpr : JavaExpr
{
    /// <summary>实例目标；静态调用为 <c>null</c>。<para>Instance target; <c>null</c> for static calls.</para></summary>
    public JavaExpr? Target { get; }

    /// <summary>所属类名（可读形式），静态调用渲染用。<para>Owning class name (readable form), used when rendering static calls.</para></summary>
    public string OwnerClass { get; }

    /// <summary>方法名。<para>Method name.</para></summary>
    public string Name { get; }

    /// <summary>返回类型（可读形式）。<para>Return type (readable form).</para></summary>
    public string ReturnType { get; }

    /// <summary>实参表达式。<para>Argument expressions.</para></summary>
    public IReadOnlyList<JavaExpr> Args { get; }

    /// <summary>是否为静态调用。<para>Whether the call is static.</para></summary>
    public bool IsStatic { get; }

    /// <summary>是否为 super 调用（invoke-super 且接收者为 this）。<para>Whether the call is a super call (invoke-super on this).</para></summary>
    public bool IsSuperCall { get; }

    /// <summary>
    /// <para>创建方法调用表达式。</para>
    /// Creates a method call expression.
    /// </summary>
    /// <param name="target">实例目标（静态时为 <c>null</c>）。<para>Instance target (<c>null</c> when static).</para></param>
    /// <param name="ownerClass">所属类名。<para>Owning class name.</para></param>
    /// <param name="name">方法名。<para>Method name.</para></param>
    /// <param name="returnType">返回类型。<para>Return type.</para></param>
    /// <param name="args">实参表达式。<para>Argument expressions.</para></param>
    /// <param name="isStatic">是否静态。<para>Whether static.</para></param>
    /// <param name="isSuperCall">是否为 super 调用。<para>Whether it is a super call.</para></param>
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

    /// <inheritdoc />
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
/// <para>对象构造表达式（new-instance + invoke-direct &lt;init&gt; 折叠）。</para>
/// Object construction expression (new-instance folded with the invoke-direct &lt;init&gt;).
/// </summary>
public sealed class NewInstanceExpr : JavaExpr
{
    /// <summary>类型名（可读形式）。<para>Type name (readable form).</para></summary>
    public string Type { get; }

    /// <summary>构造实参；未调用 &lt;init&gt; 时为 <c>null</c>。<para>Constructor arguments; <c>null</c> before &lt;init&gt; is invoked.</para></summary>
    public IReadOnlyList<JavaExpr>? Args { get; set; }

    /// <summary>
    /// <para>创建对象构造表达式。</para>
    /// Creates an object construction expression.
    /// </summary>
    /// <param name="type">类型名。<para>Type name.</para></param>
    public NewInstanceExpr(string type) => Type = type;

    /// <inheritdoc />
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
/// <para>构造器委托调用表达式（<c>this(...)</c> / <c>super(...)</c>）。</para>
/// Constructor delegation call expression (<c>this(...)</c> / <c>super(...)</c>).
/// </summary>
public sealed class ConstructorCallExpr : JavaExpr
{
    /// <summary>是否为 <c>this(...)</c>；<c>false</c> 表示 <c>super(...)</c>。<para>Whether it is <c>this(...)</c>; <c>false</c> means <c>super(...)</c>.</para></summary>
    public bool IsThisCall { get; }

    /// <summary>实参表达式。<para>Argument expressions.</para></summary>
    public IReadOnlyList<JavaExpr> Args { get; }

    /// <summary>
    /// <para>创建构造器委托调用表达式。</para>
    /// Creates a constructor delegation call expression.
    /// </summary>
    /// <param name="isThisCall">是否为 <c>this(...)</c>。<para>Whether it is <c>this(...)</c>.</para></param>
    /// <param name="args">实参表达式。<para>Argument expressions.</para></param>
    public ConstructorCallExpr(bool isThisCall, IReadOnlyList<JavaExpr> args)
    {
        IsThisCall = isThisCall;
        Args = args;
    }

    /// <inheritdoc />
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
/// <para>数组构造表达式（new-array / filled-new-array）。</para>
/// Array construction expression (new-array / filled-new-array).
/// </summary>
public sealed class NewArrayExpr : JavaExpr
{
    /// <summary>元素类型（可读形式）。<para>Element type (readable form).</para></summary>
    public string ElementType { get; }

    /// <summary>长度表达式；为 <c>null</c> 时使用初始化列表。<para>Length expression; <c>null</c> when using an initializer list.</para></summary>
    public JavaExpr? Size { get; }

    /// <summary>初始化元素；按需填充。<para>Initializer elements; filled on demand.</para></summary>
    public List<JavaExpr> Initializers { get; } = [];

    /// <summary>
    /// <para>创建数组构造表达式。</para>
    /// Creates an array construction expression.
    /// </summary>
    /// <param name="elementType">元素类型。<para>Element type.</para></param>
    /// <param name="size">长度表达式。<para>Length expression.</para></param>
    public NewArrayExpr(string elementType, JavaExpr? size)
    {
        ElementType = elementType;
        Size = size;
    }

    /// <inheritdoc />
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
/// <para>显式转型表达式（check-cast 与数值转换）。</para>
/// Explicit cast expression (check-cast and numeric conversions).
/// </summary>
public sealed class CastExpr : JavaExpr
{
    /// <summary>目标类型（可读形式）。<para>Target type (readable form).</para></summary>
    public string Type { get; }

    /// <summary>被转型表达式。<para>Inner expression.</para></summary>
    public JavaExpr Inner { get; }

    /// <summary>
    /// <para>创建显式转型表达式。</para>
    /// Creates an explicit cast expression.
    /// </summary>
    /// <param name="type">目标类型。<para>Target type.</para></param>
    /// <param name="inner">被转型表达式。<para>Inner expression.</para></param>
    public CastExpr(string type, JavaExpr inner)
    {
        Type = type;
        Inner = inner;
    }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append('(').Append(Type).Append(") ");
        Inner.WriteTo(sb);
    }
}

/// <summary>
/// <para>二元运算表达式（算术/位/比较/逻辑）。</para>
/// Binary operation expression (arithmetic/bitwise/comparison/logical).
/// </summary>
public sealed class BinaryExpr : JavaExpr
{
    /// <summary>Java 运算符（如 "+"、"=="、"&amp;&amp;"）。<para>Java operator (e.g. "+", "==", "&amp;&amp;").</para></summary>
    public string Op { get; }

    /// <summary>左操作数。<para>Left operand.</para></summary>
    public JavaExpr Left { get; }

    /// <summary>右操作数。<para>Right operand.</para></summary>
    public JavaExpr Right { get; }

    /// <summary>
    /// <para>创建二元运算表达式。</para>
    /// Creates a binary operation expression.
    /// </summary>
    /// <param name="op">Java 运算符。<para>Java operator.</para></param>
    /// <param name="left">左操作数。<para>Left operand.</para></param>
    /// <param name="right">右操作数。<para>Right operand.</para></param>
    public BinaryExpr(string op, JavaExpr left, JavaExpr right)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    /// <inheritdoc />
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
/// <para>一元运算表达式（neg/not 系列）。</para>
/// Unary operation expression (neg/not family).
/// </summary>
public sealed class UnaryExpr : JavaExpr
{
    /// <summary>Java 运算符（"-"、"!"、"~"）。<para>Java operator ("-", "!", "~").</para></summary>
    public string Op { get; }

    /// <summary>操作数。<para>Operand.</para></summary>
    public JavaExpr Inner { get; }

    /// <summary>
    /// <para>创建一元运算表达式。</para>
    /// Creates a unary operation expression.
    /// </summary>
    /// <param name="op">Java 运算符。<para>Java operator.</para></param>
    /// <param name="inner">操作数。<para>Operand.</para></param>
    public UnaryExpr(string op, JavaExpr inner)
    {
        Op = op;
        Inner = inner;
    }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb)
    {
        sb.Append(Op);
        // 对嵌套二元运算加括号，保证优先级正确。
        // Parenthesize nested binary operands to keep operator precedence correct.
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
/// <para>数组下标访问表达式（aget 系列）。</para>
/// Array index access expression (aget family).
/// </summary>
public sealed class ArrayAccessExpr : JavaExpr
{
    /// <summary>元素类型（可读形式）。<para>Element type (readable form).</para></summary>
    public string Type { get; }

    /// <summary>数组表达式。<para>Array expression.</para></summary>
    public JavaExpr Array { get; }

    /// <summary>下标表达式。<para>Index expression.</para></summary>
    public JavaExpr Index { get; }

    /// <summary>
    /// <para>创建数组下标访问表达式。</para>
    /// Creates an array index access expression.
    /// </summary>
    /// <param name="type">元素类型。<para>Element type.</para></param>
    /// <param name="array">数组表达式。<para>Array expression.</para></param>
    /// <param name="index">下标表达式。<para>Index expression.</para></param>
    public ArrayAccessExpr(string type, JavaExpr array, JavaExpr index)
    {
        Type = type;
        Array = array;
        Index = index;
    }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb)
    {
        Array.WriteTo(sb);
        sb.Append('[');
        Index.WriteTo(sb);
        sb.Append(']');
    }
}

/// <summary>
/// <para>instanceof 表达式。</para>
/// instanceof expression.
/// </summary>
public sealed class InstanceOfExpr : JavaExpr
{
    /// <summary>被检测表达式。<para>Tested expression.</para></summary>
    public JavaExpr Inner { get; }

    /// <summary>目标类型（可读形式）。<para>Target type (readable form).</para></summary>
    public string Type { get; }

    /// <summary>
    /// <para>创建 instanceof 表达式。</para>
    /// Creates an instanceof expression.
    /// </summary>
    /// <param name="inner">被检测表达式。<para>Tested expression.</para></param>
    /// <param name="type">目标类型。<para>Target type.</para></param>
    public InstanceOfExpr(JavaExpr inner, string type)
    {
        Inner = inner;
        Type = type;
    }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb)
    {
        Inner.WriteTo(sb);
        sb.Append(" instanceof ").Append(Type);
    }
}

/// <summary>
/// <para>三元条件表达式。</para>
/// Ternary conditional expression.
/// </summary>
public sealed class TernaryExpr : JavaExpr
{
    /// <summary>条件。<para>Condition.</para></summary>
    public JavaExpr Cond { get; }

    /// <summary>真分支。<para>Then branch.</para></summary>
    public JavaExpr Then { get; }

    /// <summary>假分支。<para>Else branch.</para></summary>
    public JavaExpr Else { get; }

    /// <summary>
    /// <para>创建三元条件表达式。</para>
    /// Creates a ternary conditional expression.
    /// </summary>
    /// <param name="cond">条件。<para>Condition.</para></param>
    /// <param name="then">真分支。<para>Then branch.</para></param>
    /// <param name="els">假分支。<para>Else branch.</para></param>
    public TernaryExpr(JavaExpr cond, JavaExpr then, JavaExpr els)
    {
        Cond = cond;
        Then = then;
        Else = els;
    }

    /// <inheritdoc />
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
/// <para>未知/未恢复表达式：以提示文本直出（如寄存器名）。</para>
/// Unknown/unrecovered expression: rendered as raw hint text (e.g. a register name).
/// </summary>
public sealed class UnknownExpr : JavaExpr
{
    /// <summary>提示文本。<para>Hint text.</para></summary>
    public string Hint { get; }

    /// <summary>
    /// <para>创建未知表达式。</para>
    /// Creates an unknown expression.
    /// </summary>
    /// <param name="hint">提示文本。<para>Hint text.</para></param>
    public UnknownExpr(string hint) => Hint = hint;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb) => sb.Append(Hint);
}