using UotanStudio.Firmware.Dex.Model;

namespace UotanStudio.Firmware.Dex.Decompile;

/// <summary>
/// <para>方法候选：类中声明的方法，携带可读签名与解析所需的访问标志。</para>
/// Method candidate: a method declared in a class, carrying the readable signature and the
/// access flags needed for resolution.
/// </summary>
public sealed class MethodCandidate
{
    /// <summary>所属类名（可读形式）。<para>Owning class name (readable form).</para></summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>方法名。<para>Method name.</para></summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>参数类型（可读形式）。<para>Parameter types (readable form).</para></summary>
    public string[] Parameters { get; init; } = [];

    /// <summary>返回类型（可读形式）。<para>Return type (readable form).</para></summary>
    public string ReturnType { get; init; } = string.Empty;

    /// <summary>是否静态。<para>Whether static.</para></summary>
    public bool IsStatic { get; init; }

    /// <summary>是否桥接方法（编译器为泛型擦除生成的合成重载）。<para>Whether the method is a bridge (compiler-synthesized overload for generic erasure).</para></summary>
    public bool IsBridge { get; init; }

    /// <summary>是否合成方法。<para>Whether the method is synthetic.</para></summary>
    public bool IsSynthetic { get; init; }

    /// <summary>是否为构造器。<para>Whether the method is a constructor.</para></summary>
    public bool IsConstructor { get; init; }

    /// <summary>是否私有。<para>Whether the method is private.</para></summary>
    public bool IsPrivate { get; init; }
}

/// <summary>
/// <para>重载解析结果：选中的候选 + 各实参是否需要显式转型。</para>
/// Overload resolution result: the selected candidate plus per-argument cast needs.
/// </summary>
public sealed class ResolvedCall
{
    /// <summary>选中的候选。<para>The selected candidate.</para></summary>
    public MethodCandidate Candidate { get; init; } = new();

    /// <summary>各实参是否需要显式转型到候选参数类型。<para>Whether each argument needs an explicit cast to the candidate's parameter type.</para></summary>
    public bool[] NeedsCast { get; init; } = [];

    /// <summary>解析结果是否与原始引用签名不同（需要施加转型）。<para>Whether the resolution differs from the raw referenced signature (casts needed).</para></summary>
    public bool Changed { get; init; }
}

/// <summary>
/// <para>重载解析器：为 DEX 建立「类名 + 方法名 + 元数 → 候选列表」索引，
/// 在 invoke 目标按原型精确匹配的基础上，对同名同元数候选做最特定方法选择
/// （过滤 bridge/synthetic、按推断实参类型评分、必要时要求显式转型）。</para>
/// Overload resolver: builds a "class + method name + arity → candidates" index over the DEX,
/// and on top of exact prototype matching picks the most specific candidate among same-name
/// same-arity overloads (filtering bridges/synthetics, scoring by inferred argument types,
/// requesting explicit casts when needed).
/// </summary>
public sealed class MethodResolver
{
    private readonly DexFile _dex;
    private readonly Dictionary<(string Owner, string Name, int Arity), List<MethodCandidate>> _index = new();

    /// <summary>
    /// <para>创建重载解析器并建立全 DEX 方法索引。</para>
    /// Creates an overload resolver and builds the whole-DEX method index.
    /// </summary>
    /// <param name="dex">所属 DEX 文件。<para>The owning DEX file.</para></param>
    public MethodResolver(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        _dex = dex;
        BuildIndex();
    }

    /// <summary>
    /// <para>解析一次方法调用：优先精确签名匹配，其次按推断实参类型选择最特定重载。</para>
    /// Resolves a method call: exact signature match first, then the most specific overload by
    /// inferred argument types.
    /// </summary>
    /// <param name="target">原始方法引用（invoke 操作数）。<para>The raw method reference (invoke operand).</para></param>
    /// <param name="inferredArgTypes">各实参的推断类型（可读形式，可为 <c>null</c>）。<para>Inferred argument types (readable form; entries may be <c>null</c>).</para></param>
    /// <returns>解析结果；无候选或无需调整时返回 <c>null</c>。</returns>
    public ResolvedCall? Resolve(MethodRefOperand target, string?[]? inferredArgTypes)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Name is "<init>" or "<clinit>") return null;

        // 收集类层级（自身 + 父类链 + 接口）中同名同元数的候选。
        // Collect same-name same-arity candidates from the class hierarchy (self + superclass
        // chain + interfaces).
        var candidates = CollectCandidates(target.OwnerClass, target.Name, target.Parameters.Length);
        if (candidates.Count == 0) return null;

        // 1) 精确签名匹配：找到与原始引用参数完全一致的候选。
        // 1) Exact signature match: a candidate whose parameters equal the raw reference's.
        MethodCandidate? exactNonBridge = null;
        foreach (var candidate in candidates)
        {
            if (!SameParameters(candidate.Parameters, target.Parameters)) continue;
            if (!candidate.IsBridge && !candidate.IsSynthetic)
            {
                exactNonBridge = candidate;
            }
        }

        // 精确非桥接匹配即可直接使用（无需转型）。
        // A non-bridge exact match is directly usable (no casts).
        if (exactNonBridge is not null)
        {
            return null;
        }

        // 2) 按推断实参类型对候选评分，选择最特定者。
        // 2) Score candidates by inferred argument types; pick the most specific.
        var best = SelectMostSpecific(candidates, inferredArgTypes);
        if (best is null)
        {
            // 无更特定候选：保持原始签名（含精确桥接匹配兜底）。
            // No more specific candidate: keep the raw signature (the exact bridge match included).
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

        // 若选中候选与原始签名参数完全一致（如去桥接后无更特定者），无需变更。
        // No change when the selected candidate has the exact same parameter signature.
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
        var result = new List<MethodCandidate>();
        var visited = new HashSet<string>(System.StringComparer.Ordinal);

        var current = owner;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            if (!visited.Add(current)) break;
            AddFromClass(result, current, name, arity);

            var cls = FindClass(current);
            if (cls is null || cls.SuperclassIndex < 0 || cls.SuperclassIndex >= _dex.Types.Count) break;
            current = _dex.FormatType(cls.SuperclassIndex);
        }

        // 收集接口方法（深度受限，避免接口链爆炸）。
        // Collect interface methods (bounded depth to avoid interface-chain blowup).
        foreach (var iface in InterfacesOf(owner))
        {
            AddFromClass(result, iface, name, arity);
        }

        return result;
    }

    private void AddFromClass(List<MethodCandidate> result, string className, string name, int arity)
    {
        if (!_index.TryGetValue((className, name, arity), out var candidates)) return;
        foreach (var candidate in candidates)
        {
            // 候选去重：同一签名只保留最派生类的声明。
            // De-duplicate: keep only the most-derived declaration for the same signature.
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
        var visited = new HashSet<string>(System.StringComparer.Ordinal);
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
        var typeIndex = -1;
        for (var i = 0; i < _dex.Types.Count; i++)
        {
            if (_dex.Types[i] == descriptor)
            {
                typeIndex = i;
                break;
            }
        }

        if (typeIndex < 0) return null;
        foreach (var cls in _dex.Classes)
        {
            if (cls.TypeIndex == typeIndex) return cls;
        }

        return null;
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
            if (score < 0) continue; // 推断实参不兼容

            // 同分时优先非桥接（桥接已在过滤中剔除）与签名更近的候选。
            // Prefer non-bridge candidates on ties (already filtered) and closer signatures.
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
            if (cost < 0) return -1;   // 不可兼容
            total += cost;
        }

        return total;
    }

    /// <summary>
    /// <para>实参到形参的兼容代价：精确 0、原语加宽 1、装箱/拆箱 2、引用向上 3、未知 0；不兼容 -1。</para>
    /// Compatibility cost of an argument against a parameter: exact 0, primitive widening 1,
    /// boxing/unboxing 2, reference upcast 3, unknown 0; incompatible -1.
    /// </summary>
    private static int ArgCost(string? argType, string param)
    {
        if (string.IsNullOrEmpty(argType)) return 0;   // 未知类型视为兼容
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

        // 引用到引用：简单名称等价判断（同包短名场景较少见，直接按类名比较）。
        // Reference-to-reference: a simple name-equivalence check.
        if (argType == param) return 0;
        return param == "java.lang.Object" || param == "java.lang.CharSequence" || param == "java.lang.String"
            || argType.EndsWith(param, System.StringComparison.Ordinal) ? 3 : -1;
    }
}