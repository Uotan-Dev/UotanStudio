using System.Runtime.CompilerServices;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>Entry point of the lightweight DEX decompiler; renders a parsed <c>DexFile</c> as Java-style source text.
/// <para>轻量 DEX 反编译器入口；把已解析的 <c>DexFile</c> 渲染为 Java 风格源码文本。</para></summary>
/// <example>
/// <code>
/// string source = LightweightDecompiler.DecompileAll(dex);
/// </code>
/// </example>
public static class LightweightDecompiler
{
    private static readonly ConditionalWeakTable<DexFile, CachedIndex> Indexes = [];

    /// <summary>
    /// Decompiles every class in the DEX file.
    /// <para>反编译 DEX 中的全部类。</para>
    /// </summary>
    /// <param name="dex">Parsed DEX file. <para>已解析的 DEX 文件。</para></param>
    /// <param name="options">Emission options; <see langword="null"/> uses <c>DecompileOptions.Default</c>.
    /// <para>输出选项；为 <see langword="null"/> 时使用 <c>DecompileOptions.Default</c>。</para></param>
    /// <returns>Whole-file source: a leading comment with the DEX version and element counts, followed by every rendered class.
    /// <para>完整文件源码：以包含 DEX 版本与条目计数的注释开头，随后依次渲染全部类。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is null.
    /// <para><paramref name="dex"/> 为 null。</para></exception>
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
    /// <param name="dex">Parsed DEX file owning the class. <para>拥有该类的已解析 DEX 文件。</para></param>
    /// <param name="cls">Class definition to render. <para>待渲染的类定义。</para></param>
    /// <param name="options">Emission options; <see langword="null"/> uses <c>DecompileOptions.Default</c>.
    /// <para>输出选项；为 <see langword="null"/> 时使用 <c>DecompileOptions.Default</c>。</para></param>
    /// <returns>Rendered source of the class, built with the whole-DEX overload index cached for the file.
    /// <para>使用按文件缓存的整份 DEX 重载索引生成的类源码。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="cls"/> is null.
    /// <para><paramref name="dex"/> 或 <paramref name="cls"/> 为 null。</para></exception>
    public static string DecompileClass(DexFile dex, ClassDefinition cls, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(cls);
        options ??= DecompileOptions.Default;
        return JavaCodegen.RenderClass(dex, cls, CachedResolver(dex), options);
    }

    /// <summary>
    /// Decompiles a single method.
    /// <para>反编译单个方法。</para>
    /// </summary>
    /// <param name="dex">Parsed DEX file owning the method. <para>拥有该方法的已解析 DEX 文件。</para></param>
    /// <param name="method">Encoded method to render. <para>待渲染的已编码方法。</para></param>
    /// <param name="cls">Declaring class, or <see langword="null"/> when unknown; supplying it enables class-scoped context such as the owner name.
    /// <para>声明类；未知时为 <see langword="null"/>，提供后可启用含属主名在内的类级上下文。</para></param>
    /// <param name="options">Emission options; <see langword="null"/> uses <c>DecompileOptions.Default</c>.
    /// <para>输出选项；为 <see langword="null"/> 时使用 <c>DecompileOptions.Default</c>。</para></param>
    /// <returns>Rendered source of the method, built with the whole-DEX overload index cached for the file.
    /// <para>使用按文件缓存的整份 DEX 重载索引生成的方法源码。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> or <paramref name="method"/> is null.
    /// <para><paramref name="dex"/> 或 <paramref name="method"/> 为 null。</para></exception>
    public static string DecompileMethod(DexFile dex, EncodedMethod method, ClassDefinition? cls = null, DecompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dex);
        ArgumentNullException.ThrowIfNull(method);
        options ??= DecompileOptions.Default;
        return JavaCodegen.RenderMethod(dex, method, cls, CachedResolver(dex), options);
    }

    private static MethodResolver CachedResolver(DexFile dex)
    {
        var version = new IndexVersion(dex.Classes.Count, dex.Methods.Count, dex.Protos.Count, dex.Types.Count, dex.Strings.Count);
        return Indexes.GetValue(dex, _ => new CachedIndex(new MethodResolver(dex), version)).Resolver(dex, version);
    }

    private readonly record struct IndexVersion(int Classes, int Methods, int Protos, int Types, int Strings);

    private sealed class CachedIndex(MethodResolver resolver, IndexVersion version)
    {
        private readonly Lock _gate = new();
        private MethodResolver _resolver = resolver;
        private IndexVersion _version = version;

        public MethodResolver Resolver(DexFile dex, IndexVersion version)
        {
            lock (_gate)
            {
                if (_version != version)
                {
                    _resolver = new MethodResolver(dex);
                    _version = version;
                }
                return _resolver;
            }
        }
    }
}