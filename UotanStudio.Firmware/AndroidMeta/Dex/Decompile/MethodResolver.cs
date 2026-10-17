using UotanStudio.Firmware.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.AndroidMeta.Dex.Decompile;

/// <summary>Method signature collected by the whole-DEX index for overload resolution.
/// <para>由全 DEX 索引收集、用于重载解析的方法签名。</para></summary>
internal sealed class MethodCandidate
{
    /// <summary>Human-readable name of the declaring class. <para>声明类的可读名称。</para></summary>
    /// <value>Defaults to <see cref="string.Empty"/>. <para>默认为 <see cref="string.Empty"/>。</para></value>
    public string Owner { get; init; } = string.Empty;

    /// <summary>Decoded method name. <para>解码后的方法名。</para></summary>
    /// <value>Defaults to <see cref="string.Empty"/>; <c>&lt;init&gt;</c> marks a constructor.
    /// <para>默认为 <see cref="string.Empty"/>；<c>&lt;init&gt;</c> 表示构造器。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>Human-readable parameter types in declaration order.
    /// <para>按声明顺序排列的参数可读类型。</para></summary>
    /// <value>Empty by default and when the prototype does not resolve.
    /// <para>默认以及原型无法解析时为空数组。</para></value>
    public string[] Parameters { get; init; } = [];

    /// <summary>Human-readable return type. <para>返回类型的可读形式。</para></summary>
    /// <value>Defaults to <see cref="string.Empty"/>; <c>?</c> when the prototype does not resolve.
    /// <para>默认为 <see cref="string.Empty"/>；原型无法解析时为 <c>?</c>。</para></value>
    public string ReturnType { get; init; } = string.Empty;

    /// <summary>Whether the method carries the static access flag. <para>方法是否带有 static 访问标志。</para></summary>
    /// <value>Static methods are called without a receiver. <para>static 方法调用时没有接收者。</para></value>
    public bool IsStatic { get; init; }

    /// <summary>Whether the method carries the bridge access flag. <para>方法是否带有 bridge 访问标志。</para></summary>
    /// <value>Bridge methods are skipped by overload selection. <para>重载选择会跳过 bridge 方法。</para></value>
    public bool IsBridge { get; init; }

    /// <summary>Whether the method carries the synthetic access flag. <para>方法是否带有 synthetic 访问标志。</para></summary>
    /// <value>Synthetic methods are skipped by overload selection. <para>重载选择会跳过 synthetic 方法。</para></value>
    public bool IsSynthetic { get; init; }

    /// <summary>Whether the candidate is an instance constructor. <para>候选是否为实例构造器。</para></summary>
    /// <value>True when the name equals <c>&lt;init&gt;</c>. <para>名称等于 <c>&lt;init&gt;</c> 时为 true。</para></value>
    public bool IsConstructor { get; init; }

    /// <summary>Whether the method carries the private access flag. <para>方法是否带有 private 访问标志。</para></summary>
    /// <value>Recorded for display purposes; selection does not filter on it.
    /// <para>仅供展示；重载选择不过滤该标志。</para></value>
    public bool IsPrivate { get; init; }
}

/// <summary>Overload-resolution verdict that rewrites a call site with argument casts.
/// <para>需要为调用点施加实参转型的重载解析结论。</para></summary>
internal sealed class ResolvedCall
{
    /// <summary>Most specific candidate chosen for the call.
    /// <para>为该调用选定的最特定候选。</para></summary>
    /// <value>Never <see langword="null"/>; defaults to an empty candidate.
    /// <para>不为 <see langword="null"/>；默认为空候选。</para></value>
    public MethodCandidate Candidate { get; init; } = new();

    /// <summary>Per-argument cast requirements. <para>逐实参的转型要求。</para></summary>
    /// <value>One entry per call-site parameter; <c>true</c> at index i means argument i is cast to
    /// <c>Candidate.Parameters[i]</c>. Empty by default.
    /// <para>每个调用点形参一项；索引 i 为 <c>true</c> 表示第 i 个实参转型为 <c>Candidate.Parameters[i]</c>。默认空数组。</para></value>
    public bool[] NeedsCast { get; init; } = [];

    /// <summary>Whether the resolved signature differs from the call-site signature.
    /// <para>解析所得签名与调用点签名是否存在差异。</para></summary>
    /// <value><c>MethodResolver.Resolve</c> returns instances only when this is <see langword="true"/>.
    /// <para><c>MethodResolver.Resolve</c> 仅在其为 <see langword="true"/> 时返回实例。</para></value>
    public bool Changed { get; init; }
}

/// <summary>Whole-DEX overload resolver: indexes every declared method and picks the most specific candidate
/// for a call site from inferred argument types.
/// <para>全 DEX 重载解析器：索引所有已声明方法，并按推断实参类型为调用点挑选最特定的候选。</para></summary>
internal sealed class MethodResolver
{
    private readonly DexFile _dex;
    private readonly Dictionary<(string Owner, string Name, int Arity), List<MethodCandidate>> _index = new();
    private readonly Dictionary<(string Owner, string Name, int Arity), List<MethodCandidate>> _candidates = new();
    private readonly Dictionary<string, int> _typeIndices = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ClassDefinition> _classes = [];

    /// <summary>
    /// Creates an overload resolver and builds the whole-DEX method index.
    /// <para>创建重载解析器并建立全 DEX 方法索引。</para>
    /// </summary>
    /// <param name="dex">Dex file whose classes and methods are indexed at construction time.
    /// <para>构造时即建立类与方法索引的 DEX 文件。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is null.
    /// <para><paramref name="dex"/> 为 null。</para></exception>
    public MethodResolver(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        _dex = dex;
        for (var index = 0; index < dex.Types.Count; index++)
        {
            _typeIndices.TryAdd(dex.Types[index], index);
        }
        foreach (var cls in dex.Classes)
        {
            _classes.TryAdd(cls.TypeIndex, cls);
        }
        BuildIndex();
    }

    /// <summary>
    /// Resolves a method call: exact signature match first, then the most specific overload by inferred argument types.
    /// <para>解析一次方法调用：优先精确签名匹配，其次按推断实参类型选择最特定重载。</para>
    /// </summary>
    /// <param name="target">Method reference operand taken from the call instruction.
    /// <para>来自调用指令的方法引用操作数。</para></param>
    /// <param name="inferredArgTypes">Readable type hint per argument; entries may be <see langword="null"/>, and a
    /// <see langword="null"/> array scores every argument as unknown.
    /// <para>逐实参的可读类型提示；对应项可为 <see langword="null"/>，整个数组为 <see langword="null"/> 时所有实参按未知处理。</para></param>
    /// <returns>A rewritten-call verdict when the most specific overload differs from the call-site signature;
    /// <see langword="null"/> for constructors and class initializers, when no candidate is found, when a non-bridge,
    /// non-synthetic candidate already matches the call-site signature exactly, or when no argument needs a cast.
    /// <para>当最特定重载与调用点签名存在差异时返回改写结论；构造器与类初始化器、无候选、已有非 bridge 且非
    /// synthetic 的精确匹配、或无需任何实参转型时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.
    /// <para><paramref name="target"/> 为 null。</para></exception>
    public ResolvedCall? Resolve(MethodRefOperand target, string?[]? inferredArgTypes)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Name is "<init>" or "<clinit>") return null;


        var candidates = CollectCandidates(target.OwnerClass, target.Name, target.Parameters.Length);
        if (candidates.Count == 0) return null;


        MethodCandidate? exactNonBridge = null;
        foreach (var candidate in candidates)
        {
            if (!SameParameters(candidate.Parameters, target.Parameters)) continue;
            if (!candidate.IsBridge && !candidate.IsSynthetic)
            {
                exactNonBridge = candidate;
            }
        }


        if (exactNonBridge is not null)
        {
            return null;
        }


        var best = SelectMostSpecific(candidates, inferredArgTypes);
        if (best is null)
        {


            return null;
        }

        var needsCast = new bool[target.Parameters.Length];
        var changed = false;
        for (var i = 0; i < needsCast.Length; i++)
        {
            var same = i < best.Parameters.Length && best.Parameters[i] == (i < target.Parameters.Length ? target.Parameters[i] : null);
            if (!same)
            {
                needsCast[i] = true;
                changed = true;
            }
        }


        if (!changed) return null;

        return new ResolvedCall { Candidate = best, NeedsCast = needsCast, Changed = true };
    }

    private void BuildIndex()
    {
        foreach (var cls in _dex.Classes)
        {
            var owner = _dex.FormatType(cls.TypeIndex);
            foreach (var method in cls.AllMethods)
            {
                var methodId = _dex.GetMethod(method.MethodIndex);
                if (methodId is null) continue;

                var proto = _dex.GetProto(methodId.ProtoIndex);
                var name = _dex.GetString(methodId.NameIndex);

                string[] parameters;
                if (proto is null)
                {
                    parameters = [];
                }
                else
                {
                    var indices = proto.ParameterTypeIndices;
                    parameters = new string[indices.Length];
                    for (var i = 0; i < indices.Length; i++)
                    {
                        parameters[i] = _dex.FormatType(indices[i]);
                    }
                }

                var candidate = new MethodCandidate
                {
                    Owner = owner,
                    Name = name,
                    Parameters = parameters,
                    ReturnType = proto is null ? "?" : _dex.FormatType(proto.ReturnTypeIndex),
                    IsStatic = (method.AccessFlags & AccessFlags.Static) != 0,
                    IsBridge = (method.AccessFlags & AccessFlags.Bridge) != 0,
                    IsSynthetic = (method.AccessFlags & AccessFlags.Synthetic) != 0,
                    IsConstructor = name == "<init>",
                    IsPrivate = (method.AccessFlags & AccessFlags.Private) != 0
                };

                var key = (owner, name, parameters.Length);
                if (!_index.TryGetValue(key, out var list))
                {
                    _index[key] = list = [];
                }

                list.Add(candidate);
            }
        }
    }

    private List<MethodCandidate> CollectCandidates(string owner, string name, int arity)
    {
        var key = (owner, name, arity);
        if (_candidates.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = new List<MethodCandidate>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var current = owner;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            if (!visited.Add(current)) break;
            AddFromClass(result, current, name, arity);

            var cls = FindClass(current);
            if (cls is null || cls.SuperclassIndex < 0 || cls.SuperclassIndex >= _dex.Types.Count) break;
            current = _dex.FormatType(cls.SuperclassIndex);
        }


        foreach (var iface in InterfacesOf(owner))
        {
            AddFromClass(result, iface, name, arity);
        }

        _candidates.Add(key, result);
        return result;
    }

    private void AddFromClass(List<MethodCandidate> result, string className, string name, int arity)
    {
        if (!_index.TryGetValue((className, name, arity), out var candidates)) return;
        foreach (var candidate in candidates)
        {


            var duplicate = false;
            foreach (var existing in result)
            {
                if (existing.Owner == candidate.Owner && existing.Name == candidate.Name
                    && SameParameters(existing.Parameters, candidate.Parameters))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate) result.Add(candidate);
        }
    }

    private IEnumerable<string> InterfacesOf(string owner)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var cls = FindClass(owner);
        if (cls is null) yield break;

        foreach (var index in cls.InterfaceTypeIndices)
        {
            if ((uint)index < _dex.Types.Count)
            {
                queue.Enqueue(_dex.FormatType(index));
            }
        }

        while (queue.Count > 0 && visited.Count < 16)
        {
            var name = queue.Dequeue();
            if (!visited.Add(name)) continue;
            yield return name;

            var iface = FindClass(name);
            if (iface is null) continue;
            foreach (var index in iface.InterfaceTypeIndices)
            {
                if ((uint)index < _dex.Types.Count)
                {
                    queue.Enqueue(_dex.FormatType(index));
                }
            }
        }
    }

    private ClassDefinition? FindClass(string readableName)
    {
        var descriptor = DescriptorRenderer.ToDescriptor(readableName);
        return _typeIndices.TryGetValue(descriptor, out var typeIndex) && _classes.TryGetValue(typeIndex, out var cls)
            ? cls
            : null;
    }

    private static bool SameParameters(string[] left, string[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
        }

        return true;
    }

    private static MethodCandidate? SelectMostSpecific(List<MethodCandidate> candidates, string?[]? inferred)
    {
        MethodCandidate? best = null;
        var bestScore = int.MaxValue;
        var bestBridgePenalty = false;

        foreach (var candidate in candidates)
        {
            if (candidate.IsBridge || candidate.IsSynthetic) continue;

            var score = ScoreCandidate(candidate, inferred);
            if (score < 0) continue;


            if (score < bestScore || (score == bestScore && bestBridgePenalty))
            {
                best = candidate;
                bestScore = score;
                bestBridgePenalty = false;
            }
        }

        return best;
    }

    private static int ScoreCandidate(MethodCandidate candidate, string?[]? inferred)
    {
        var total = 0;
        for (var i = 0; i < candidate.Parameters.Length; i++)
        {
            var param = candidate.Parameters[i];
            var argType = inferred is not null && i < inferred.Length ? inferred[i] : null;
            var cost = ArgCost(argType, param);
            if (cost < 0) return -1;
            total += cost;
        }

        return total;
    }

    /// <summary>
    /// Compatibility cost of an argument against a parameter: exact 0, primitive widening 1, boxing/unboxing 2, reference upcast 3, unknown 0; incompatible -1.
    /// <para>实参到形参的兼容代价：精确 0、原语加宽 1、装箱/拆箱 2、引用向上 3、未知 0；不兼容 -1。</para>
    /// </summary>
    private static int ArgCost(string? argType, string param)
    {
        if (string.IsNullOrEmpty(argType)) return 0;
        if (argType == param) return 0;
        if (argType == "null" || argType == "?" || argType == "java.lang.Object") return 3;

        var argPrim = DescriptorRenderer.IsPrimitive(argType);
        var paramPrim = DescriptorRenderer.IsPrimitive(param);

        if (argPrim && paramPrim)
        {
            return DescriptorRenderer.IsWideningPrimitive(argType, param) ? 1 : -1;
        }

        if (argPrim && !paramPrim)
        {
            return DescriptorRenderer.Boxed(argType) == param ? 2 : 3;
        }

        if (!argPrim && paramPrim)
        {
            return argType == DescriptorRenderer.Boxed(param) ? 2 : -1;
        }


        if (argType == param) return 0;
        return param == "java.lang.Object" || param == "java.lang.CharSequence" || param == "java.lang.String"
            || argType.EndsWith(param, StringComparison.Ordinal) ? 3 : -1;
    }
}