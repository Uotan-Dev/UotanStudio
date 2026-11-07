using BenchmarkDotNet.Attributes;
using ZLinq;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>
/// Compares the per-item query shapes the DEX writer, the EROFS builder and the filesystem metadata
/// generator run once per class, per field and per inode against the same shapes over a ZLinq value
/// enumerable, so the adoption decision rests on measured allocation and time instead of the raw LINQ
/// call-site count.
/// <para>把 DEX 写入器、EROFS 构建器与文件系统元数据生成器按类、按字段、按 inode 执行的逐项查询形状，与
/// 同样的形状在 ZLinq 值枚举上的实现对比，使采用决定建立在实测分配与耗时之上，而不是 LINQ 调用点数量。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class ZLinqAdoptionBenchmarks
{
    private const int ClassCount = 2_000;
    private const int InterfacesPerClass = 4;
    private const int FieldsPerClass = 8;
    private const int PclustersPerInode = 6;
    private const int XattrsPerInode = 3;
    private const int BlockSize = 4096;
    private const string CapabilityXattrName = "security.capability";

    private int[] _types = [];
    private int[][] _interfaces = [];
    private FieldSlot[][] _fields = [];
    private Pcluster[][] _pclusters = [];
    private XattrSlot[][] _xattrs = [];

    [GlobalSetup]
    public void Setup()
    {
        _types = new int[64];
        for (var i = 0; i < _types.Length; i++)
        {
            _types[i] = i * 7;
        }

        _interfaces = new int[ClassCount][];
        _fields = new FieldSlot[ClassCount][];
        _pclusters = new Pcluster[ClassCount][];
        _xattrs = new XattrSlot[ClassCount][];
        for (var cls = 0; cls < ClassCount; cls++)
        {
            var interfaces = new int[InterfacesPerClass];
            for (var i = 0; i < interfaces.Length; i++)
            {
                interfaces[i] = (cls + i) % _types.Length;
            }

            _interfaces[cls] = interfaces;

            var fields = new FieldSlot[FieldsPerClass];
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i] = new FieldSlot((cls * 3 + i) % 61);
            }

            _fields[cls] = fields;

            var pclusters = new Pcluster[PclustersPerInode];
            for (var i = 0; i < pclusters.Length; i++)
            {
                pclusters[i] = new Pcluster(BlockSize - (i * 137) - (cls % 89));
            }

            _pclusters[cls] = pclusters;

            var xattrs = new XattrSlot[XattrsPerInode];
            for (var i = 0; i < xattrs.Length; i++)
            {
                xattrs[i] = new XattrSlot(i == xattrs.Length - 1 && cls % 3 == 0 ? CapabilityXattrName : "user.test");
            }

            _xattrs[cls] = xattrs;
        }
    }

    /// <summary>
    /// Relocates a class's interface indices through the type table, deduplicates and orders them — the
    /// per-class step <c>SmaliAssembler.Context.Canonicalize</c> performs.
    /// <para>把类的接口索引经类型表重定位、去重并排序，即 <c>SmaliAssembler.Context.Canonicalize</c> 逐类执行的步骤。</para>
    /// </summary>
    [Benchmark(Baseline = true)]
    public int LinqInterfaceRelocation()
    {
        var total = 0;
        foreach (var interfaces in _interfaces)
        {
            var relocated = interfaces.Select(index => _types[index]).Distinct().Order().ToArray();
            total += relocated.Length;
        }

        return total;
    }

    /// <inheritdoc cref="LinqInterfaceRelocation"/>
    [Benchmark]
    public int ZLinqInterfaceRelocation()
    {
        var total = 0;
        foreach (var interfaces in _interfaces)
        {
            var relocated = interfaces.AsValueEnumerable().Select(index => _types[index]).Distinct().Order().ToArray();
            total += relocated.Length;
        }

        return total;
    }

    /// <summary>
    /// Detects duplicate field indices within one class via a distinct count — the per-class guard
    /// <c>SmaliAssembler.Context.Canonicalize</c> raises on a re-declared field.
    /// <para>通过去重计数检测同一类内重复的字段索引，即 <c>SmaliAssembler.Context.Canonicalize</c> 在重复声明字段时抛出的逐类校验。</para>
    /// </summary>
    [Benchmark]
    public int LinqFieldDuplicateGuard()
    {
        var duplicates = 0;
        foreach (var fields in _fields)
        {
            if (fields.Select(field => field.Index).Distinct().Count() != fields.Length)
            {
                duplicates++;
            }
        }

        return duplicates;
    }

    /// <inheritdoc cref="LinqFieldDuplicateGuard"/>
    [Benchmark]
    public int ZLinqFieldDuplicateGuard()
    {
        var duplicates = 0;
        foreach (var fields in _fields)
        {
            if (fields.AsValueEnumerable().Select(field => field.Index).Distinct().Count() != fields.Length)
            {
                duplicates++;
            }
        }

        return duplicates;
    }

    /// <summary>
    /// Sums the block count of one inode's compressed clusters — the per-inode step the EROFS layout
    /// planner runs for every payload node.
    /// <para>汇总一个 inode 压缩簇的块数，即 EROFS 布局规划器对每个负载节点执行的逐 inode 步骤。</para>
    /// </summary>
    [Benchmark]
    public long LinqPclusterBlockSum()
    {
        long blocks = 0;
        foreach (var pclusters in _pclusters)
        {
            blocks += pclusters.Sum(pcluster => (long)CeilDiv(pcluster.CompressedLength, BlockSize));
        }

        return blocks;
    }

    /// <inheritdoc cref="LinqPclusterBlockSum"/>
    [Benchmark]
    public long ZLinqPclusterBlockSum()
    {
        long blocks = 0;
        foreach (var pclusters in _pclusters)
        {
            blocks += pclusters.AsValueEnumerable().Sum(pcluster => (long)CeilDiv(pcluster.CompressedLength, BlockSize));
        }

        return blocks;
    }

    /// <summary>
    /// Scans one inode's xattr list for the capability record — the per-inode lookup the filesystem
    /// metadata generator runs for every EROFS inode while exporting fs_config.
    /// <para>在一个 inode 的 xattr 列表中查找能力记录，即文件系统元数据生成器导出 fs_config 时对每个
    /// EROFS inode 执行的逐 inode 查找。</para>
    /// </summary>
    [Benchmark]
    public int LinqXattrCapabilityLookup()
    {
        var hits = 0;
        foreach (var xattrs in _xattrs)
        {
            if (xattrs.FirstOrDefault(xattr => xattr.Name == CapabilityXattrName) is not null)
            {
                hits++;
            }
        }

        return hits;
    }

    /// <inheritdoc cref="LinqXattrCapabilityLookup"/>
    [Benchmark]
    public int ZLinqXattrCapabilityLookup()
    {
        var hits = 0;
        foreach (var xattrs in _xattrs)
        {
            if (xattrs.AsValueEnumerable().FirstOrDefault(xattr => xattr.Name == CapabilityXattrName) is not null)
            {
                hits++;
            }
        }

        return hits;
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    private readonly record struct FieldSlot(int Index);

    private readonly record struct Pcluster(int CompressedLength);

    private sealed class XattrSlot(string name)
    {
        public string Name { get; } = name;
    }
}