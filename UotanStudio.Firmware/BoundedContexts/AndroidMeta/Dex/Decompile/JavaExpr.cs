using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public abstract class JavaExpr
{
    /// <summary>
    /// Writes the expression into the string builder.
    /// <para>把表达式写入字符串构建器。</para>
    /// </summary>
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

public sealed class LiteralExpr : JavaExpr
{
    public long Value { get; }

    /// <summary>
    /// Creates a numeric literal expression.
    /// <para>创建数值字面量表达式。</para>
    /// </summary>
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

public sealed class StringExpr : JavaExpr
{
    public string Value { get; }

    /// <summary>
    /// Creates a string literal expression.
    /// <para>创建字符串字面量表达式。</para>
    /// </summary>
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

public sealed class ClassLiteralExpr : JavaExpr
{
    public string TypeName { get; }

    /// <summary>
    /// Creates a class literal expression.
    /// <para>创建类字面量表达式。</para>
    /// </summary>
    public ClassLiteralExpr(string typeName) => TypeName = typeName;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append(TypeName).Append(".class");
}

public sealed class NullExpr : JavaExpr
{
    public static NullExpr Instance { get; } = new();

    private NullExpr() { }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append("null");
}

public sealed class LocalExpr : JavaExpr
{
    public string Name { get; }

    public string? Type { get; }

    /// <summary>
    /// Creates a local register expression.
    /// <para>创建局部寄存器表达式。</para>
    /// </summary>
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

public sealed class FieldExpr : JavaExpr
{
    public JavaExpr? Target { get; }

    public string OwnerClass { get; }

    public string Name { get; }

    public string Type { get; }

    public bool IsStatic { get; }

    /// <summary>
    /// Creates a field access expression.
    /// <para>创建字段访问表达式。</para>
    /// </summary>
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

public sealed class MethodCallExpr : JavaExpr
{
    public JavaExpr? Target { get; }

    public string OwnerClass { get; }

    public string Name { get; }

    public string ReturnType { get; }

    public IReadOnlyList<JavaExpr> Args { get; }

    public bool IsStatic { get; }

    public bool IsSuperCall { get; }

    /// <summary>
    /// Creates a method call expression.
    /// <para>创建方法调用表达式。</para>
    /// </summary>
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

public sealed class NewInstanceExpr : JavaExpr
{
    public string Type { get; }

    public IReadOnlyList<JavaExpr>? Args { get; set; }

    /// <summary>
    /// Creates an object construction expression.
    /// <para>创建对象构造表达式。</para>
    /// </summary>
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

public sealed class ConstructorCallExpr : JavaExpr
{
    public bool IsThisCall { get; }

    public IReadOnlyList<JavaExpr> Args { get; }

    /// <summary>
    /// Creates a constructor delegation call expression.
    /// <para>创建构造器委托调用表达式。</para>
    /// </summary>
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

public sealed class NewArrayExpr : JavaExpr
{
    public string ElementType { get; }

    public JavaExpr? Size { get; }

    public List<JavaExpr> Initializers { get; } = [];

    /// <summary>
    /// Creates an array construction expression.
    /// <para>创建数组构造表达式。</para>
    /// </summary>
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

public sealed class CastExpr : JavaExpr
{
    public string Type { get; }

    public JavaExpr Inner { get; }

    /// <summary>
    /// Creates an explicit cast expression.
    /// <para>创建显式转型表达式。</para>
    /// </summary>
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

public sealed class BinaryExpr : JavaExpr
{
    public string Op { get; }

    public JavaExpr Left { get; }

    public JavaExpr Right { get; }

    /// <summary>
    /// Creates a binary operation expression.
    /// <para>创建二元运算表达式。</para>
    /// </summary>
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

public sealed class UnaryExpr : JavaExpr
{
    public string Op { get; }

    public JavaExpr Inner { get; }

    /// <summary>
    /// Creates a unary operation expression.
    /// <para>创建一元运算表达式。</para>
    /// </summary>
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

public sealed class ArrayAccessExpr : JavaExpr
{
    public string Type { get; }

    public JavaExpr Array { get; }

    public JavaExpr Index { get; }

    /// <summary>
    /// Creates an array index access expression.
    /// <para>创建数组下标访问表达式。</para>
    /// </summary>
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

public sealed class InstanceOfExpr : JavaExpr
{
    public JavaExpr Inner { get; }

    public string Type { get; }

    /// <summary>
    /// Creates an instanceof expression.
    /// <para>创建 instanceof 表达式。</para>
    /// </summary>
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

public sealed class TernaryExpr : JavaExpr
{
    public JavaExpr Cond { get; }

    public JavaExpr Then { get; }

    public JavaExpr Else { get; }

    /// <summary>
    /// Creates a ternary conditional expression.
    /// <para>创建三元条件表达式。</para>
    /// </summary>
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

public sealed class UnknownExpr : JavaExpr
{
    public string Hint { get; }

    /// <summary>
    /// Creates an unknown expression.
    /// <para>创建未知表达式。</para>
    /// </summary>
    public UnknownExpr(string hint) => Hint = hint;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb) => sb.Append(Hint);
}