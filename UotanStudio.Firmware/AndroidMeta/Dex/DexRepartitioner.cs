using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// Repartitions DEX classes across multiple dex images so every part stays inside the 16-bit
/// identifier-table limits (the "64K method" limit). Classes travel as disassembler text and are
/// re-assembled per part, so each part receives its own canonically ordered tables.
/// <para>把 DEX 类重新分配到多个 dex 镜像，使每个分区都保持在 16 位标识符表上限内（即"64K 方法"上限）。
/// 类以反汇编文本为载体，按分区重新汇编，因此每个分区拥有自己的规范排序标识符表。</para>
/// </summary>
/// <remarks>
/// The assembly step is also the limit validator: a part is only accepted when its re-assembled
/// type, proto, field and method tables fit the index range; ranges that do not fit are bisected
/// on class boundaries until every part validates. Class order inside each part follows the input
/// order of the source images. Legacy-device main-dex lists are not computed — callers must
/// re-verify application startup after repartitioning.
/// <para>汇编步骤同时充当上限校验器：分区只有在其重新汇编出的类型、原型、字段与方法表都落在索引范围内时才被
/// 接受；不满足的类区间会按类边界二分，直到所有分区都通过。分区内的类顺序保持源镜像的输入顺序。
/// 本类不计算旧设备的 main-dex 列表——调用方在重新分配后必须自行验证应用启动。</para>
/// </remarks>
public static class DexRepartitioner
{
    /// <summary>The DEX 16-bit identifier-table limit enforced per output part.
    /// <para>每个输出分区强制执行的 DEX 16 位标识符表上限。</para></summary>
    public const int MaximumIndexedEntries = 0x10000;

    // Declared-count budgets used by the greedy pre-pack. Referenced identifiers from code
    // (invokes, field accesses, type uses) add on top of declared members, so the budgets keep a
    // margin below the hard limit; the assembly validation is the authoritative check.
    private const int DeclaredMethodBudget = 60_000;
    private const int DeclaredFieldBudget = 60_000;

    /// <summary>Splits one DEX image into as many parts as the identifier-table limits require.
    /// <para>把单个 DEX 镜像拆分为标识符表上限所需的若干分区。</para></summary>
    /// <param name="dexBytes">Complete DEX image bytes of a version accepted by <see cref="DexFile.Load(byte[])"/>.
    /// <para>完整的 DEX 镜像字节，版本须被 <see cref="DexFile.Load(byte[])"/> 接受。</para></param>
    /// <param name="cancellationToken">Cancels between parse, disassembly and assembly steps.
    /// <para>在解析、反汇编与汇编步骤之间取消。</para></param>
    /// <returns>A single-element list holding <paramref name="dexBytes"/> unchanged when the image already fits the limits; otherwise one rebuilt image per part, in class order.
    /// <para>当镜像已在上限内时返回仅含原字节不变的单元素列表；否则按类顺序返回每个分区一个重建镜像。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dexBytes"/> is <see langword="null"/>.
    /// <para><paramref name="dexBytes"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DexException">A single class already exceeds the limits, or an intermediate image cannot be re-assembled.
    /// <para>单个类已超出上限，或中间镜像无法重新汇编。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static IReadOnlyList<byte[]> Split(byte[] dexBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dexBytes);
        var dex = DexFile.Load(dexBytes);
        return Split(dex, dexBytes, cancellationToken);
    }

    /// <summary>Splits an already parsed DEX model into as many parts as the identifier-table limits require.
    /// <para>把已解析的 DEX 模型拆分为标识符表上限所需的若干分区。</para></summary>
    /// <param name="dex">Parsed model; not modified. <para>已解析的模型；不被修改。</para></param>
    /// <param name="originalBytes">The original image bytes returned unchanged when the model already fits the limits; <see langword="null"/> re-serializes the input even for a single part.
    /// <para>原始镜像字节；模型已满足上限时原样返回；为 <see langword="null"/> 时即使单分区也会重新序列化。</para></param>
    /// <param name="cancellationToken">Cancels between disassembly and assembly steps.
    /// <para>在反汇编与汇编步骤之间取消。</para></param>
    /// <returns>One rebuilt image per part, in class order. <para>按类顺序返回每个分区一个重建镜像。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DexException">A single class already exceeds the limits, or an intermediate image cannot be re-assembled.
    /// <para>单个类已超出上限，或中间镜像无法重新汇编。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static IReadOnlyList<byte[]> Split(DexFile dex, byte[]? originalBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dex);
        cancellationToken.ThrowIfCancellationRequested();
        if (FitsLimits(dex) && originalBytes is not null) return [originalBytes];
        var blocks = DisassembleClasses(dex, cancellationToken);
        return PackBlocks(blocks, dex.Header.Version, cancellationToken);
    }

    /// <summary>Repacks the classes of several DEX images into the minimum number of valid parts.
    /// <para>把多个 DEX 镜像的类重新打包为数量最少的合法分区。</para></summary>
    /// <param name="dexParts">Complete DEX images whose classes are redistributed together.
    /// <para>参与类重新分配的完整 DEX 镜像。</para></param>
    /// <param name="cancellationToken">Cancels between parse, disassembly and assembly steps.
    /// <para>在解析、反汇编与汇编步骤之间取消。</para></param>
    /// <returns>Rebuilt images numbered by the caller; when all classes fit one part the list has a single entry. The inputs are never modified.
    /// <para>重建镜像，编号由调用方处理；全部类可容纳于一个分区时列表只含一个条目。输入不被修改。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="dexParts"/> is empty or contains a null element.
    /// <para><paramref name="dexParts"/> 为空或含 null 元素。</para></exception>
    /// <exception cref="DexException">A single class already exceeds the limits, or an intermediate image cannot be re-assembled.
    /// <para>单个类已超出上限，或中间镜像无法重新汇编。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static IReadOnlyList<byte[]> Rebalance(IReadOnlyList<byte[]> dexParts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dexParts);
        if (dexParts.Count == 0) throw new ArgumentException("At least one DEX part is required.", nameof(dexParts));
        cancellationToken.ThrowIfCancellationRequested();

        var blocks = new List<ClassBlock>();
        var version = "035";
        foreach (var part in dexParts)
        {
            ArgumentNullException.ThrowIfNull(part);
            var dex = DexFile.Load(part);
            if (string.CompareOrdinal(dex.Header.Version, version) > 0) version = dex.Header.Version;
            blocks.AddRange(DisassembleClasses(dex, cancellationToken));
        }
        if (blocks.Count == 0) throw new DexException("The container holds no DEX classes to repartition.");
        return PackBlocks(blocks, version, cancellationToken);
    }

    /// <summary>Produces the canonical smali block of every class together with its declared member counts.
    /// <para>产出每个类的规范 smali 文本块及其声明成员计数。</para></summary>
    private static List<ClassBlock> DisassembleClasses(DexFile dex, CancellationToken cancellationToken)
    {
        var blocks = new List<ClassBlock>(dex.Classes.Count);
        foreach (var cls in dex.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blocks.Add(new ClassBlock(
                Disassembler.DisassembleClass(dex, cls),
                cls.DirectMethods.Count + cls.VirtualMethods.Count,
                cls.StaticFields.Count + cls.InstanceFields.Count));
        }
        return blocks;
    }

    // Greedy-packs class blocks in order, then validates each part by re-assembly and bisects failures.
    private static IReadOnlyList<byte[]> PackBlocks(List<ClassBlock> blocks, string version, CancellationToken cancellationToken)
    {
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        var methods = 0;
        var fields = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (start < i && (methods + block.DeclaredMethods > DeclaredMethodBudget
                || fields + block.DeclaredFields > DeclaredFieldBudget))
            {
                ranges.Add((start, i));
                start = i;
                methods = 0;
                fields = 0;
            }
            methods += block.DeclaredMethods;
            fields += block.DeclaredFields;
        }
        if (start < blocks.Count) ranges.Add((start, blocks.Count));

        var parts = new List<byte[]>();
        foreach (var (rangeStart, rangeEnd) in ranges)
        {
            parts.AddRange(PackRange(blocks, version, rangeStart, rangeEnd, cancellationToken));
        }
        return parts;
    }

    /// <summary>Assembles the class range into one image when it fits the limits, otherwise bisects it on a class boundary.
    /// <para>类区间满足上限时汇编为单个镜像，否则按类边界二分。</para></summary>
    private static IEnumerable<byte[]> PackRange(
        List<ClassBlock> blocks, string version, int start, int end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = TryAssemble(blocks, version, start, end);
        if (attempt.Image is not null)
        {
            return [attempt.Image];
        }
        if (end - start == 1)
        {
            throw new DexException(
                $"Class block {start} cannot be repartitioned: {attempt.Error ?? "it exceeds the DEX identifier-table limits"}. "
                + $"类块 {start} 无法重新分配：{attempt.Error ?? "超出 DEX 标识符表上限"}。");
        }
        var mid = start + (end - start) / 2;
        var left = PackRange(blocks, version, start, mid, cancellationToken);
        var right = PackRange(blocks, version, mid, end, cancellationToken);
        return left.Concat(right).ToArray();
    }

    /// <summary>Assembles the class range and reports whether the rebuilt image fits the identifier-table limits.
    /// <para>汇编类区间并报告重建镜像是否满足标识符表上限。</para></summary>
    private static (byte[]? Image, string? Error) TryAssemble(
        List<ClassBlock> blocks, string version, int start, int end)
    {
        var sb = new StringBuilder(4096);
        for (var i = start; i < end; i++)
        {
            sb.AppendLine(blocks[i].Text);
        }
        DexFile model;
        try
        {
            model = SmaliAssembler.Assemble(sb.ToString());
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
        if (!FitsLimits(model)) return (null, null);
        return (ApplyVersion(model, version).ToArray(), null);
    }

    /// <summary>Reports whether every 16-bit indexed table of the model stays inside the DEX limit.
    /// <para>报告模型的每张 16 位索引表是否都落在 DEX 上限之内。</para></summary>
    private static bool FitsLimits(DexFile dex) =>
        dex.Types.Count <= MaximumIndexedEntries
        && dex.Protos.Count <= MaximumIndexedEntries
        && dex.Fields.Count <= MaximumIndexedEntries
        && dex.Methods.Count <= MaximumIndexedEntries;

    /// <summary>Copies the assembled model into a fresh model carrying the highest source version, so method-handle-era features keep their declared magic.
    /// <para>把汇编出的模型复制进携带最高源版本的新模型，使方法句柄时代的特性保留其声明的魔数。</para></summary>
    private static DexFile ApplyVersion(DexFile model, string version)
    {
        if (version == model.Header.Version) return model;
        var copy = new DexFile(version);
        copy.Strings.AddRange(model.Strings);
        copy.Types.AddRange(model.Types);
        copy.Protos.AddRange(model.Protos);
        copy.Fields.AddRange(model.Fields);
        copy.Methods.AddRange(model.Methods);
        copy.Classes.AddRange(model.Classes);
        copy.MethodHandles.AddRange(model.MethodHandles);
        copy.CallSites.AddRange(model.CallSites);
        return copy;
    }

    /// <summary>One disassembled class plus its declared member counts used by the greedy packer.
    /// <para>单个反汇编类及其声明成员计数，供贪心装包使用。</para></summary>
    private readonly record struct ClassBlock(string Text, int DeclaredMethods, int DeclaredFields);
}