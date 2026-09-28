using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UotanStudio.Firmware.DeviceTree.Dt;
using DtTree = UotanStudio.Firmware.DeviceTree.Dt.DeviceTree;

namespace UotanStudio.Firmware.DeviceTree.Dt;

internal static class DtOverlayApplier
{
    internal static DtTree Apply(DtTree baseTree, DtTree overlayTree)
    {
        var result = Clone(baseTree);
        var overlay = Clone(overlayTree);
        var fragments = overlay.Root.Children.Where(n => n.FindChild("__overlay__") is not null).ToList();
        if (fragments.Count == 0)
        {
            DtsFormat.MergeNode(result.Root, overlay.Root);
        }
        else
        {
            var delta = Nodes(result.Root).Select(GetPhandle).DefaultIfEmpty().Max();
            foreach (var node in Nodes(overlay.Root))
                foreach (var name in new[] { "phandle", "linux,phandle" })
                    if (node.FindProperty(name) is { } property)
                        SetCell(property, 0, AddPhandle(ReadCell(property, 0), delta));

            if (overlay.Root.FindChild("__local_fixups__") is { } local)
                FixLocal(overlay.Root, local, value => AddPhandle(value, delta));
            ResolveExternal(result, overlay);

            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fragment in fragments)
            {
                var target = ResolveTarget(result, fragment);
                var contents = fragment.FindChild("__overlay__")!;
                PreserveHandles(target, contents, overlay);
                targets[fragment.Name] = target.GetPath();
                var mergedContents = new DeviceTreeNode("__overlay__");
                Copy(contents, mergedContents);
                DtsFormat.MergeNode(target, mergedContents);
            }
            if (overlay.Root.FindChild("__symbols__") is { } symbols)
            {
                var output = result.Root.FindChild("__symbols__") ?? result.Root.AddChild("__symbols__");
                foreach (var property in symbols.Properties)
                {
                    var path = property.GetString();
                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || parts[1] != "__overlay__" || !targets.TryGetValue(parts[0], out var targetPath))
                        throw new InvalidDataException("Overlay symbol is outside a fragment overlay.");
                    var destination = targetPath.TrimEnd('/') + "/" + string.Join('/', parts.Skip(2));
                    destination = destination.Length > 1 ? destination.TrimEnd('/') : destination;
                    SetProperty(output, property.Name, Encoding.UTF8.GetBytes(destination + '\0'));
                }
            }
        }
        foreach (var property in baseTree.Root.Properties.ToList()) baseTree.Root.RemoveProperty(property.Name);
        foreach (var child in baseTree.Root.Children.ToList()) baseTree.Root.RemoveChild(child);
        DtsFormat.MergeNode(baseTree.Root, result.Root);
        return baseTree;
    }

    private static DtTree Clone(DtTree source)
    {
        var tree = new DtTree { BootCpuId = source.BootCpuId, Version = source.Version };
        Copy(source.Root, tree.Root);
        foreach (var pair in source.LabelPaths) tree.LabelPaths.Add(pair.Key, pair.Value);
        return tree;
    }

    private static void Copy(DeviceTreeNode source, DeviceTreeNode target)
    {
        foreach (var property in source.Properties) target.AddProperty(property.Name, property.Value);
        foreach (var child in source.Children) Copy(child, target.AddChild(child.Name));
    }

    private static IEnumerable<DeviceTreeNode> Nodes(DeviceTreeNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in Nodes(child)) yield return descendant;
    }

    private static uint GetPhandle(DeviceTreeNode node)
    {
        var property = node.FindProperty("phandle") ?? node.FindProperty("linux,phandle");
        return property is null ? 0 : ReadCell(property, 0);
    }

    private static uint AddPhandle(uint value, uint delta)
    {
        if (value == 0 || value == uint.MaxValue || delta >= uint.MaxValue - value)
            throw new InvalidDataException("Overlay phandle is invalid or overflows.");
        return value + delta;
    }

    private static uint ReadCell(DeviceTreeProperty property, int offset)
    {
        if (offset < 0 || (offset & 3) != 0 || offset > property.Length - 4)
            throw new InvalidDataException("Overlay cell offset is outside its property.");
        return BinaryPrimitives.ReadUInt32BigEndian(property.Value[offset..]);
    }

    private static void SetCell(DeviceTreeProperty property, int offset, uint value)
    {
        _ = ReadCell(property, offset);
        var bytes = property.ValueBytes;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        property.ReplaceValue(bytes);
    }

    private static void FixLocal(DeviceTreeNode node, DeviceTreeNode fixups, Func<uint, uint> update)
    {
        foreach (var fixup in fixups.Properties)
        {
            var property = node.FindProperty(fixup.Name) ?? throw new InvalidDataException("Local fixup property does not exist.");
            if ((fixup.Length & 3) != 0) throw new InvalidDataException("Invalid local fixup offset array.");
            foreach (var offset in fixup.GetCells())
                SetCell(property, checked((int)offset), update(ReadCell(property, checked((int)offset))));
        }
        foreach (var child in fixups.Children)
            FixLocal(node.FindChild(child.Name) ?? throw new InvalidDataException("Local fixup node does not exist."), child, update);
    }

    private static void ResolveExternal(DtTree tree, DtTree overlay)
    {
        if (overlay.Root.FindChild("__fixups__") is not { } fixups) return;
        var max = Nodes(tree.Root).Select(GetPhandle).DefaultIfEmpty().Max();
        foreach (var fixup in fixups.Properties)
        {
            var path = tree.Root.FindChild("__symbols__")?.FindProperty(fixup.Name)?.GetString()
                ?? (tree.LabelPaths.TryGetValue(fixup.Name, out var labelPath) ? labelPath : null);
            var target = path is not null ? tree.FindNode(path) : null;
            if (target is null) throw new InvalidDataException($"Overlay symbol '{fixup.Name}' is unresolved.");
            var handle = GetPhandle(target);
            if (handle == 0)
            {
                handle = AddPhandle(1, max);
                max = handle;
                target.AddPropertyCells("phandle", handle);
            }
            if (fixup.Length == 0 || fixup.Value[^1] != 0) throw new InvalidDataException("External fixups must be NUL terminated.");
            foreach (var location in Encoding.UTF8.GetString(fixup.Value).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = location.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    throw new InvalidDataException("Invalid external fixup location.");
                var property = overlay.FindNode(parts[0])?.FindProperty(parts[1])
                    ?? throw new InvalidDataException("External fixup target does not exist.");
                SetCell(property, offset, handle);
            }
        }
    }

    private static DeviceTreeNode ResolveTarget(DtTree tree, DeviceTreeNode fragment)
    {
        if (fragment.FindProperty("target") is { } target)
        {
            if (target.Length != 4) throw new InvalidDataException("Invalid fragment target.");
            var phandle = ReadCell(target, 0);
            if (phandle is 0 or uint.MaxValue) throw new InvalidDataException("Invalid fragment target phandle.");
            return Nodes(tree.Root).FirstOrDefault(node => GetPhandle(node) == phandle)
                ?? throw new InvalidDataException("Fragment target phandle does not exist.");
        }
        var path = fragment.FindProperty("target-path")?.GetString();
        return path is not null && path.StartsWith('/') && tree.FindNode(path) is { } node ? node
            : throw new InvalidDataException("Fragment target path does not exist.");
    }

    private static void PreserveHandles(DeviceTreeNode target, DeviceTreeNode contents, DtTree overlay)
    {
        var oldHandle = GetPhandle(contents);
        var existing = GetPhandle(target);
        if (oldHandle != 0 && existing != 0 && oldHandle != existing)
        {
            foreach (var node in Nodes(overlay.Root))
                foreach (var name in new[] { "phandle", "linux,phandle" })
                    if (node.FindProperty(name) is { } property && ReadCell(property, 0) == oldHandle)
                        SetCell(property, 0, existing);
            if (overlay.Root.FindChild("__local_fixups__") is { } local)
                FixLocal(overlay.Root, local, value => value == oldHandle ? existing : value);
        }
        foreach (var child in contents.Children)
            if (target.FindChild(child.Name) is { } match) PreserveHandles(match, child, overlay);
    }

    private static void SetProperty(DeviceTreeNode node, string name, ReadOnlySpan<byte> value)
    {
        if (node.FindProperty(name) is { } existing) existing.ReplaceValue(value);
        else node.AddProperty(name, value);
    }
}