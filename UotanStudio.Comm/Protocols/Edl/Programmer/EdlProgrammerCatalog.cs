namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// An immutable, validated set of programmer descriptors offered for identity matching.
/// <para>用于身份匹配的不可变、已校验 programmer 描述符集合。</para>
/// The catalog never resolves a programmer from file names, model names or directory layout; every
/// admission decision is recorded so a rejected catalog build can be explained.
/// <para>目录绝不依据文件名、机型名或目录结构解析 programmer；每次入册决定均有记录，
/// 被拒绝的目录构建可解释。</para>
/// </summary>
public sealed class EdlProgrammerCatalog
{
    /// <summary>
    /// Gets the catalog that contains no descriptors.
    /// <para>获取不包含任何描述符的目录。</para>
    /// </summary>
    public static EdlProgrammerCatalog Empty { get; } = new([]);

    /// <summary>
    /// Initializes the catalog with its validated descriptor list.
    /// <para>以已校验的描述符列表初始化目录。</para>
    /// </summary>
    /// <param name="descriptors">
    /// The already validated, duplicate-free descriptors. <para>已校验且无重复的描述符。</para>
    /// </param>
    private EdlProgrammerCatalog(IReadOnlyList<EdlProgrammerDescriptor> descriptors)
    {
        Descriptors = descriptors;
    }

    /// <summary>
    /// Gets the admitted descriptors in declaration order.
    /// <para>获取按声明顺序入册的描述符。</para>
    /// </summary>
    /// <value>The immutable descriptor list. <para>不可变的描述符列表。</para></value>
    public IReadOnlyList<EdlProgrammerDescriptor> Descriptors { get; }

    /// <summary>
    /// Builds a catalog, rejecting incomplete, malformed and duplicate-identity descriptors.
    /// <para>构建目录，拒绝不完整、格式错误与身份重复的描述符。</para>
    /// </summary>
    /// <param name="descriptors">
    /// The candidate descriptors. <para>候选描述符。</para>
    /// </param>
    /// <returns>The validated catalog. <para>已校验的目录。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="descriptors"/> or one of its items is <see langword="null"/>.
    /// <para><paramref name="descriptors"/> 或其某一项为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">
    /// A descriptor failed validation, or two descriptors claim the same SHA-256 identity.
    /// <para>某个描述符校验失败，或两个描述符声称同一 SHA-256 身份。</para></exception>
    public static EdlProgrammerCatalog FromDescriptors(IEnumerable<EdlProgrammerDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var materialized = new List<EdlProgrammerDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            descriptor.Validate();
            if (!seen.Add(descriptor.Sha256Hex))
            {
                throw new ArgumentException(
                    $"Duplicate programmer identity: {descriptor.FileName} repeats SHA-256 {descriptor.Sha256Hex}.",
                    nameof(descriptors));
            }

            materialized.Add(descriptor);
        }

        return new EdlProgrammerCatalog(materialized);
    }
}