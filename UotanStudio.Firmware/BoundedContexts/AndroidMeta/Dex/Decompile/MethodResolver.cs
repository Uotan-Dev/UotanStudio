using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;

public sealed class MethodCandidate
{
    public string Owner { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string[] Parameters { get; init; } = [];

    public string ReturnType { get; init; } = string.Empty;

    public bool IsStatic { get; init; }

    public bool IsBridge { get; init; }

    public bool IsSynthetic { get; init; }

    public bool IsConstructor { get; init; }

    public bool IsPrivate { get; init; }
}

public sealed class ResolvedCall
{
    public MethodCandidate Candidate { get; init; } = new();

    public bool[] NeedsCast { get; init; } = [];

    public bool Changed { get; init; }
}

public sealed class MethodResolver
{
    private readonly DexFile _dex;
    private readonly Dictionary<(string Owner, string Name, int Arity), List<MethodCandidate>> _index = new();

    /// <summary>
    /// Creates an overload resolver and builds the whole-DEX method index.
    /// <para>创建重载解析器并建立全 DEX 方法索引。</para>
    /// </summary>
    public MethodResolver(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        _dex = dex;
        BuildIndex();
    }

    /// <summary>
    /// Resolves a method call: exact signature match first, then the most specific overload by inferred argument types.
    /// <para>解析一次方法调用：优先精确签名匹配，其次按推断实参类型选择最特定重载。</para>
    /// </summary>
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
            || argType.EndsWith(param, System.StringComparison.Ordinal) ? 3 : -1;
    }
}