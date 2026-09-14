using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public abstract class JavaStmt
{
    public int Offset { get; set; } = -1;

    /// <summary>
    /// Writes the statement into the builder at the given indentation level.
    /// <para>按指定缩进层级写入字符串构建器。</para>
    /// </summary>
    public abstract void WriteTo(StringBuilder sb, int indent, DecompileOptions options);

    /// <summary>
    /// Writes indentation whitespace.
    /// <para>写入缩进空白。</para>
    /// </summary>
    protected static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++) sb.Append("    ");
    }

    /// <summary>
    /// Writes a condition expression: binary expressions omit their outer parentheses (conditional contexts such as if/while provide the parentheses themselves).
    /// <para>写入条件表达式：二元表达式省略最外层括号（if/while 等条件上下文自带括号）。</para>
    /// </summary>
    protected static void WriteCondition(StringBuilder sb, JavaExpr cond)
    {
        if (cond is BinaryExpr binary)
        {
            binary.Left.WriteTo(sb);
            sb.Append(' ').Append(binary.Op).Append(' ');
            binary.Right.WriteTo(sb);
        }
        else
        {
            cond.WriteTo(sb);
        }
    }
}

public sealed class AssignStmt : JavaStmt
{
    public JavaExpr Target { get; }

    public JavaExpr Value { get; }

    /// <summary>
    /// Creates an assignment statement.
    /// <para>创建赋值语句。</para>
    /// </summary>
    public AssignStmt(JavaExpr target, JavaExpr value)
    {
        Target = target;
        Value = value;
    }

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        Target.WriteTo(sb);
        sb.Append(" = ");
        Value.WriteTo(sb);
        sb.Append(';');
    }
}

public sealed class ExprStmt : JavaStmt
{
    public JavaExpr Expr { get; }

    /// <summary>
    /// Creates an expression statement.
    /// <para>创建表达式语句。</para>
    /// </summary>
    public ExprStmt(JavaExpr expr) => Expr = expr;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        Expr.WriteTo(sb);
        sb.Append(';');
    }
}

public sealed class ReturnStmt : JavaStmt
{
    public JavaExpr? Value { get; }

    /// <summary>
    /// Creates a return statement.
    /// <para>创建返回语句。</para>
    /// </summary>
    public ReturnStmt(JavaExpr? value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("return");
        if (Value is not null)
        {
            sb.Append(' ');
            Value.WriteTo(sb);
        }
        sb.Append(';');
    }
}

public sealed class ThrowStmt : JavaStmt
{
    public JavaExpr Value { get; }

    /// <summary>
    /// Creates a throw statement.
    /// <para>创建抛出语句。</para>
    /// </summary>
    public ThrowStmt(JavaExpr value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("throw ");
        Value.WriteTo(sb);
        sb.Append(';');
    }
}

public sealed class RawCommentStmt : JavaStmt
{
    public string Text { get; }

    /// <summary>
    /// Creates a comment statement.
    /// <para>创建注释语句。</para>
    /// </summary>
    public RawCommentStmt(string text) => Text = text;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("// ").Append(Text);
    }
}

public sealed class BlockStmt : JavaStmt
{
    public List<JavaStmt> Statements { get; } = [];

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.AppendLine("{");
        foreach (var stmt in Statements)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

public sealed class IfStmt : JavaStmt
{
    public JavaExpr Cond { get; }

    public List<JavaStmt> Then { get; } = [];

    public List<JavaStmt> Else { get; } = [];

    /// <summary>
    /// Creates an if statement.
    /// <para>创建 if 语句。</para>
    /// </summary>
    public IfStmt(JavaExpr cond) => Cond = cond;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("if (");
        WriteCondition(sb, Cond);
        sb.AppendLine(") {");
        foreach (var stmt in Then)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        if (Else.Count == 0)
        {
            sb.Append('}');
        }
        else if (Else.Count == 1 && Else[0] is IfStmt nestedIf)
        {
            sb.Append("} else ");


            sb.Append("if (");
            WriteCondition(sb, nestedIf.Cond);
            sb.AppendLine(") {");
            foreach (var stmt in nestedIf.Then)
            {
                stmt.WriteTo(sb, indent + 1, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent);
            if (nestedIf.Else.Count > 0)
            {
                sb.Append("} else {");
                sb.AppendLine();
                foreach (var stmt in nestedIf.Else)
                {
                    stmt.WriteTo(sb, indent + 1, options);
                    sb.AppendLine();
                }
                WriteIndent(sb, indent);
            }
            sb.Append('}');
        }
        else
        {
            sb.Append("} else {");
            sb.AppendLine();
            foreach (var stmt in Else)
            {
                stmt.WriteTo(sb, indent + 1, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent);
            sb.Append('}');
        }
    }
}

public sealed class WhileStmt : JavaStmt
{
    public JavaExpr Cond { get; }

    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// Creates a while statement.
    /// <para>创建 while 语句。</para>
    /// </summary>
    public WhileStmt(JavaExpr cond) => Cond = cond;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("while (");
        WriteCondition(sb, Cond);
        sb.AppendLine(") {");
        foreach (var stmt in Body)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

public sealed class DoWhileStmt : JavaStmt
{
    public JavaExpr Cond { get; }

    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// Creates a do-while statement.
    /// <para>创建 do-while 语句。</para>
    /// </summary>
    public DoWhileStmt(JavaExpr cond) => Cond = cond;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.AppendLine("do {");
        foreach (var stmt in Body)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append("} while (");
        WriteCondition(sb, Cond);
        sb.Append(");");
    }
}

public sealed class InfiniteLoopStmt : JavaStmt
{
    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.AppendLine("while (true) {");
        foreach (var stmt in Body)
        {
            stmt.WriteTo(sb, indent + 1, options);
            sb.AppendLine();
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

public sealed class SwitchStmt : JavaStmt
{
    public JavaExpr Value { get; }

    public List<(long Key, List<JavaStmt> Body)> Cases { get; } = [];

    public List<JavaStmt> DefaultBody { get; } = [];

    /// <summary>
    /// Creates a switch statement.
    /// <para>创建 switch 语句。</para>
    /// </summary>
    public SwitchStmt(JavaExpr value) => Value = value;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("switch (");
        Value.WriteTo(sb);
        sb.AppendLine(") {");
        foreach (var (key, body) in Cases)
        {
            WriteIndent(sb, indent + 1);
            sb.Append("case ").Append(key.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(": {");
            foreach (var stmt in body)
            {
                stmt.WriteTo(sb, indent + 2, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent + 1);
            sb.AppendLine("break;");
            WriteIndent(sb, indent + 1);
            sb.AppendLine("}");
        }
        if (DefaultBody.Count > 0)
        {
            WriteIndent(sb, indent + 1);
            sb.AppendLine("default: {");
            foreach (var stmt in DefaultBody)
            {
                stmt.WriteTo(sb, indent + 2, options);
                sb.AppendLine();
            }
            WriteIndent(sb, indent + 1);
            sb.AppendLine("}");
        }
        WriteIndent(sb, indent);
        sb.Append('}');
    }
}

public sealed class BreakStmt : JavaStmt
{
    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("break;");
    }
}

public sealed class GotoCommentStmt : JavaStmt
{
    public string Label { get; }

    /// <summary>
    /// Creates a goto guidance comment.
    /// <para>创建 goto 指导注释。</para>
    /// </summary>
    public GotoCommentStmt(string label) => Label = label;

    /// <summary>
    /// Writes the Java representation to the output.
    /// <para>向输出写入 Java 表示。</para>
    /// </summary>
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("// goto ").Append(Label);
    }
}