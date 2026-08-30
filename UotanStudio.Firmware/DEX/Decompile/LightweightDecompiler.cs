using System.Text;
using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>轻量化反编译引擎入口：把 DEX 的类/方法还原为伪 Java 代码（或字节码指导文本），
/// 内置重载解析；参考 jadx 的 CFG 结构化思路，砍掉 SSA/内联/lambda/泛型还原等重件。</para>
/// Lightweight decompiler entry: restores DEX classes/methods into pseudo-Java code (or bytecode
/// guidance text) with built-in overload resolution; follows jadx's CFG-structuring idea while
/// dropping the heavy machinery (SSA/inlining/lambda/generics).
/// </summary>
public static class LightweightDecompiler
{
    /// <summary>
    /// <para>反编译 DEX 中的全部类。</para>
    /// Decompiles every class in the DEX file.
    /// </summary>
    /// <param name="dex">DEX 文件模型。<para>The DEX file model.</para></param>
    /// <param name="options">反编译选项；<c>null</c> 用默认值。<para>Decompiler options; <c>null</c> for defaults.</para></param>
    /// <returns>伪 Java 源码文本。<para>The pseudo-Java source text.</para></returns>
    public static string DecompileAll(DexFile dex, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        options ??= DecompileOptions.Default;

        var resolver = new MethodResolver(dex);
        var sb = new StringBuilder(4096);
        sb.Append("// Decompiled by UotanStudio lightweight DEX decompiler (jadx-inspired)\n")
          .Append("// DEX v").Append(dex.Header.Version)
          .Append(" | classes: ").Append(dex.Classes.Count)
          .Append(" | methods: ").Append(dex.Methods.Count)
          .AppendLine();

        foreach (var cls in dex.Classes)
        {
            sb.AppendLine();
            sb.Append(JavaCodegen.RenderClass(dex, cls, resolver, options));
        }

        return sb.ToString();
    }

    /// <summary>
    /// <para>反编译单个类。</para>
    /// Decompiles a single class.
    /// </summary>
    /// <param name="dex">DEX 文件模型。<para>The DEX file model.</para></param>
    /// <param name="cls">类定义。<para>The class definition.</para></param>
    /// <param name="options">反编译选项；<c>null</c> 用默认值。<para>Decompiler options; <c>null</c> for defaults.</para></param>
    /// <returns>伪 Java 源码文本。<para>The pseudo-Java source text.</para></returns>
    public static string DecompileClass(DexFile dex, ClassDefinition cls, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(cls);
        options ??= DecompileOptions.Default;
        return JavaCodegen.RenderClass(dex, cls, new MethodResolver(dex), options);
    }

    /// <summary>
    /// <para>反编译单个方法。</para>
    /// Decompiles a single method.
    /// </summary>
    /// <param name="dex">DEX 文件模型。<para>The DEX file model.</para></param>
    /// <param name="method">编码方法。<para>The encoded method.</para></param>
    /// <param name="cls">所属类定义（可空）。<para>The owning class definition (optional).</para></param>
    /// <param name="options">反编译选项；<c>null</c> 用默认值。<para>Decompiler options; <c>null</c> for defaults.</para></param>
    /// <returns>伪 Java 方法文本。<para>The pseudo-Java method text.</para></returns>
    public static string DecompileMethod(DexFile dex, EncodedMethod method, ClassDefinition? cls = null, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(method);
        options ??= DecompileOptions.Default;
        return JavaCodegen.RenderMethod(dex, method, cls, new MethodResolver(dex), options);
    }
}
