using System.Collections.Generic;
using System.Text;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>伪 Java 语句基类：结构化控制流恢复后的语句树节点，负责按缩进渲染。</para>
/// Base class of pseudo-Java statements: statement tree nodes produced after structured
/// control-flow recovery, each rendering itself with indentation.
/// </summary>
public abstract class JavaStmt
{
    /// <summary>
    /// <para>源字节码偏移（字节），用于 BytecodeGuide 模式的行首标注；无来源为 -1。</para>
    /// Source bytecode offset (bytes), prefixed in BytecodeGuide mode; -1 when none.
    /// </summary>
    public int Offset { get; set; } = -1;

    /// <summary>
    /// <para>按指定缩进层级写入字符串构建器。</para>
    /// Writes the statement into the builder at the given indentation level.
    /// </summary>
    /// <param name="sb">目标构建器。<para>Target builder.</para></param>
    /// <param name="indent">缩进层级。<para>Indentation level.</para></param>
    /// <param name="options">反编译选项。<para>Decompiler options.</para></param>
    public abstract void WriteTo(StringBuilder sb, int indent, DecompileOptions options);

    /// <summary>
    /// <para>写入缩进空白。</para>
    /// Writes indentation whitespace.
    /// </summary>
    protected static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++) sb.Append("    ");
    }

    /// <summary>
    /// <para>写入条件表达式：二元表达式省略最外层括号（if/while 等条件上下文自带括号）。</para>
    /// Writes a condition expression: binary expressions omit their outer parentheses (conditional
    /// contexts such as if/while provide the parentheses themselves).
    /// </summary>
    /// <param name="sb">目标构建器。<para>Target builder.</para></param>
    /// <param name="cond">条件表达式。<para>Condition expression.</para></param>
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

/// <summary>
/// <para>赋值语句（move / iget / sget / aget 系列）。</para>
/// Assignment statement (move / iget / sget / aget family).
/// </summary>
public sealed class AssignStmt : JavaStmt
{
    /// <summary>赋值目标（局部寄存器 / 字段 / 数组元素）。<para>Assignment target (local / field / array element).</para></summary>
    public JavaExpr Target { get; }

    /// <summary>被赋的值。<para>Assigned value.</para></summary>
    public JavaExpr Value { get; }

    /// <summary>
    /// <para>创建赋值语句。</para>
    /// Creates an assignment statement.
    /// </summary>
    /// <param name="target">赋值目标。<para>Assignment target.</para></param>
    /// <param name="value">被赋的值。<para>Assigned value.</para></param>
    public AssignStmt(JavaExpr target, JavaExpr value)
    {
        Target = target;
        Value = value;
    }

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        Target.WriteTo(sb);
        sb.Append(" = ");
        Value.WriteTo(sb);
        sb.Append(';');
    }
}

/// <summary>
/// <para>表达式语句（方法调用、数组填充等副作用语句）。</para>
/// Expression statement (method call, array fill and other side-effecting statements).
/// </summary>
public sealed class ExprStmt : JavaStmt
{
    /// <summary>表达式。<para>Expression.</para></summary>
    public JavaExpr Expr { get; }

    /// <summary>
    /// <para>创建表达式语句。</para>
    /// Creates an expression statement.
    /// </summary>
    /// <param name="expr">表达式。<para>Expression.</para></param>
    public ExprStmt(JavaExpr expr) => Expr = expr;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        Expr.WriteTo(sb);
        sb.Append(';');
    }
}

/// <summary>
/// <para>返回语句。</para>
/// Return statement.
/// </summary>
public sealed class ReturnStmt : JavaStmt
{
    /// <summary>返回值；<c>null</c> 表示 return-void。<para>Return value; <c>null</c> for return-void.</para></summary>
    public JavaExpr? Value { get; }

    /// <summary>
    /// <para>创建返回语句。</para>
    /// Creates a return statement.
    /// </summary>
    /// <param name="value">返回值；<c>null</c> 表示 return-void。<para>Return value; <c>null</c> for return-void.</para></param>
    public ReturnStmt(JavaExpr? value) => Value = value;

    /// <inheritdoc />
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

/// <summary>
/// <para>抛出语句。</para>
/// Throw statement.
/// </summary>
public sealed class ThrowStmt : JavaStmt
{
    /// <summary>被抛出的异常表达式。<para>Thrown exception expression.</para></summary>
    public JavaExpr Value { get; }

    /// <summary>
    /// <para>创建抛出语句。</para>
    /// Creates a throw statement.
    /// </summary>
    /// <param name="value">被抛出的异常表达式。<para>Thrown exception expression.</para></param>
    public ThrowStmt(JavaExpr value) => Value = value;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("throw ");
        Value.WriteTo(sb);
        sb.Append(';');
    }
}

/// <summary>
/// <para>原始注释语句：无法结构化或无法恢复的指令以注释保留。</para>
/// Raw comment statement: instructions that cannot be structured or recovered are kept as comments.
/// </summary>
public sealed class RawCommentStmt : JavaStmt
{
    /// <summary>注释文本（不含前缀）。<para>Comment text (without the prefix).</para></summary>
    public string Text { get; }

    /// <summary>
    /// <para>创建注释语句。</para>
    /// Creates a comment statement.
    /// </summary>
    /// <param name="text">注释文本。<para>Comment text.</para></param>
    public RawCommentStmt(string text) => Text = text;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("// ").Append(Text);
    }
}

/// <summary>
/// <para>语句块。</para>
/// Statement block.
/// </summary>
public sealed class BlockStmt : JavaStmt
{
    /// <summary>块内语句。<para>Statements in the block.</para></summary>
    public List<JavaStmt> Statements { get; } = [];

    /// <inheritdoc />
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

/// <summary>
/// <para>if 语句（含 else 分支；else 仅含单个 if 时渲染为 else if）。</para>
/// if statement (with an optional else branch; a lone if in the else renders as else if).
/// </summary>
public sealed class IfStmt : JavaStmt
{
    /// <summary>条件表达式。<para>Condition expression.</para></summary>
    public JavaExpr Cond { get; }

    /// <summary>真分支语句。<para>Then-branch statements.</para></summary>
    public List<JavaStmt> Then { get; } = [];

    /// <summary>假分支语句；为空表示无 else。<para>Else-branch statements; empty when there is no else.</para></summary>
    public List<JavaStmt> Else { get; } = [];

    /// <summary>
    /// <para>创建 if 语句。</para>
    /// Creates an if statement.
    /// </summary>
    /// <param name="cond">条件表达式。<para>Condition expression.</para></param>
    public IfStmt(JavaExpr cond) => Cond = cond;

    /// <inheritdoc />
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
            // 渲染为 else if (...) { ... }：手动展开嵌套 if 头与块。
            // Render as "else if (...) { ... }": expand the nested if header and block manually.
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

/// <summary>
/// <para>while 语句。</para>
/// while statement.
/// </summary>
public sealed class WhileStmt : JavaStmt
{
    /// <summary>循环条件。<para>Loop condition.</para></summary>
    public JavaExpr Cond { get; }

    /// <summary>循环体。<para>Loop body.</para></summary>
    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// <para>创建 while 语句。</para>
    /// Creates a while statement.
    /// </summary>
    /// <param name="cond">循环条件。<para>Loop condition.</para></param>
    public WhileStmt(JavaExpr cond) => Cond = cond;

    /// <inheritdoc />
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

/// <summary>
/// <para>do-while 语句。</para>
/// do-while statement.
/// </summary>
public sealed class DoWhileStmt : JavaStmt
{
    /// <summary>循环条件。<para>Loop condition.</para></summary>
    public JavaExpr Cond { get; }

    /// <summary>循环体。<para>Loop body.</para></summary>
    public List<JavaStmt> Body { get; } = [];

    /// <summary>
    /// <para>创建 do-while 语句。</para>
    /// Creates a do-while statement.
    /// </summary>
    /// <param name="cond">循环条件。<para>Loop condition.</para></param>
    public DoWhileStmt(JavaExpr cond) => Cond = cond;

    /// <inheritdoc />
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

/// <summary>
/// <para>无限循环语句（while (true)）。</para>
/// Infinite loop statement (while (true)).
/// </summary>
public sealed class InfiniteLoopStmt : JavaStmt
{
    /// <summary>循环体。<para>Loop body.</para></summary>
    public List<JavaStmt> Body { get; } = [];

    /// <inheritdoc />
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

/// <summary>
/// <para>switch 语句（case 键为 long；default 分支可选）。</para>
/// switch statement (case keys are long; the default branch is optional).
/// </summary>
public sealed class SwitchStmt : JavaStmt
{
    /// <summary>开关值表达式。<para>Switch value expression.</para></summary>
    public JavaExpr Value { get; }

    /// <summary>各 case 分支（键升序）。<para>Case branches (keys ascending).</para></summary>
    public List<(long Key, List<JavaStmt> Body)> Cases { get; } = [];

    /// <summary>default 分支；为空表示无。<para>Default branch; empty when absent.</para></summary>
    public List<JavaStmt> DefaultBody { get; } = [];

    /// <summary>
    /// <para>创建 switch 语句。</para>
    /// Creates a switch statement.
    /// </summary>
    /// <param name="value">开关值表达式。<para>Switch value expression.</para></param>
    public SwitchStmt(JavaExpr value) => Value = value;

    /// <inheritdoc />
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

/// <summary>
/// <para>break 语句。</para>
/// break statement.
/// </summary>
public sealed class BreakStmt : JavaStmt
{
    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("break;");
    }
}

/// <summary>
/// <para>goto 指导注释：未结构化的控制流以字节码标签注释保留（方法指导模式）。</para>
/// Goto guidance comment: unstructured control flow is kept as bytecode label comments (method-guide mode).
/// </summary>
public sealed class GotoCommentStmt : JavaStmt
{
    /// <summary>目标标签文本（如 "label_0010"）。<para>Target label text (e.g. "label_0010").</para></summary>
    public string Label { get; }

    /// <summary>
    /// <para>创建 goto 指导注释。</para>
    /// Creates a goto guidance comment.
    /// </summary>
    /// <param name="label">目标标签。<para>Target label.</para></param>
    public GotoCommentStmt(string label) => Label = label;

    /// <inheritdoc />
    public override void WriteTo(StringBuilder sb, int indent, DecompileOptions options)
    {
        WriteIndent(sb, indent);
        sb.Append("// goto ").Append(Label);
    }
}
