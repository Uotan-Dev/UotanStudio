using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public static class LightweightDecompiler
{
    /// <summary>
    /// Decompiles every class in the DEX file.
    /// <para>反编译 DEX 中的全部类。</para>
    /// </summary>
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
    /// Decompiles a single class.
    /// <para>反编译单个类。</para>
    /// </summary>
    public static string DecompileClass(DexFile dex, ClassDefinition cls, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(cls);
        options ??= DecompileOptions.Default;
        return JavaCodegen.RenderClass(dex, cls, new MethodResolver(dex), options);
    }

    /// <summary>
    /// Decompiles a single method.
    /// <para>反编译单个方法。</para>
    /// </summary>
    public static string DecompileMethod(DexFile dex, EncodedMethod method, ClassDefinition? cls = null, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(method);
        options ??= DecompileOptions.Default;
        return JavaCodegen.RenderMethod(dex, method, cls, new MethodResolver(dex), options);
    }
}