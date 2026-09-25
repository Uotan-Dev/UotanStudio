using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Reads bounded text scatter metadata, supporting flat and storage-grouped layouts.</summary>
public static class MtkScatterReader
{
    /// <summary>Parses metadata without resolving file paths, choosing a device or authorizing writes.</summary>
    /// <param name="source">Caller-owned UTF-8 stream, left open and consumed from its current position.</param>
    /// <param name="cancellationToken">Cancellation for bounded input reading and YAML events.</param>
    /// <returns>Metadata retaining disabled entries, literal addresses and unknown scalar fields.</returns>
    public static MtkScatterDocument Read(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        using var text = new StreamReader(source, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var content = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = text.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content.Length > 1024 * 1024 - count)
            {
                throw Invalid("Scatter text exceeds the 1048576-character limit.");
            }
            content.Append(buffer, 0, count);
        }
        using var input = new StringReader(content.ToString());
        var yaml = new YamlStream();
        try
        {
            yaml.Load(new BoundedParser(new Parser(input), cancellationToken));
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException("Invalid scatter YAML.", exception);
        }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlSequenceNode root
            || root.Children.Count < 2)
        {
            throw Invalid("Scatter requires one document with a platform header and layouts.");
        }
        var general = Map(root.Children[0]);
        RequireKeys(general, "general", "info");
        if (Scalar(general["general"]) != "MTK_PLATFORM_CFG")
        {
            throw Invalid("Unexpected scatter platform header.");
        }
        var info = SingleInfo(general["info"]);
        var version = Required(info, "config_version");
        var storages = new List<MtkScatterStorage>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var totalPartitions = 0;
        if (Map(root.Children[1]).ContainsKey("partition_index"))
        {
            storages.Add(Storage(Required(info, "storage"), info, root.Children.Skip(1), ref totalPartitions));
        }
        else
        {
            foreach (var node in root.Children.Skip(1))
            {
                var layout = Map(node);
                RequireKeys(layout, "storage_type", "description");
                var name = Scalar(layout["storage_type"]);
                if (storages.Count >= 8 || !names.Add(name)
                    || layout["description"] is not YamlSequenceNode description || description.Children.Count < 2)
                {
                    throw Invalid("Invalid, duplicate or excessive scatter storage layouts.");
                }
                var header = Map(description.Children[0]);
                RequireKeys(header, "general", "info");
                if (Scalar(header["general"]) != "MTK_STORAGE_CFG")
                {
                    throw Invalid("Unexpected scatter storage header.");
                }
                var fields = SingleInfo(header["info"]);
                if (Required(fields, "storage") != name)
                {
                    throw Invalid("Scatter storage names disagree.");
                }
                storages.Add(Storage(name, fields, description.Children.Skip(1), ref totalPartitions));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MtkScatterDocument(version, Required(info, "platform"), Required(info, "project"),
            info, storages.AsReadOnly());
    }

    private static MtkScatterStorage Storage(string name, IReadOnlyDictionary<string, string> fields,
        IEnumerable<YamlNode> entries, ref int totalPartitions)
    {
        var partitions = new List<MtkScatterPartition>();
        var indices = new HashSet<uint>();
        foreach (var node in entries)
        {
            if (++totalPartitions > 4096)
            {
                throw Invalid("Too many scatter partitions.");
            }
            var entry = Fields(node);
            var index = Required(entry, "partition_index");
            if (!index.StartsWith("SYS", StringComparison.Ordinal)
                || !uint.TryParse(index.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || !indices.Add(number))
            {
                throw Invalid("Invalid or duplicate scatter partition index.");
            }
            partitions.Add(new MtkScatterPartition(index, Required(entry, "partition_name"), Required(entry, "file_name"),
                Boolean(entry, "is_download"), Required(entry, "type"), Number(entry, "linear_start_addr"),
                Number(entry, "physical_start_addr"), Number(entry, "partition_size"), Required(entry, "region"),
                Required(entry, "storage"), Boolean(entry, "boundary_check"), Boolean(entry, "is_reserved"),
                Required(entry, "operation_type"), entry));
        }
        var blockSize = Number(fields, "block_size");
        if (blockSize == 0 || partitions.Count == 0)
        {
            throw Invalid("Scatter requires a positive block-size hint and partition entries.");
        }
        return new MtkScatterStorage(name, Required(fields, "boot_channel"), blockSize, fields, partitions.AsReadOnly());
    }

    private static IReadOnlyDictionary<string, string> SingleInfo(YamlNode node)
    {
        if (node is not YamlSequenceNode sequence || sequence.Children.Count != 1)
        {
            throw Invalid("Scatter info must contain exactly one mapping.");
        }
        return Fields(sequence.Children[0]);
    }

    private static Dictionary<string, YamlNode> Map(YamlNode node)
    {
        if (node is not YamlMappingNode map || map.Children.Count > 64)
        {
            throw Invalid("Expected a bounded scatter mapping.");
        }
        var fields = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in map.Children)
        {
            var key = Scalar(pair.Key);
            if (key.Length > 128 || !fields.TryAdd(key, pair.Value))
            {
                throw Invalid("Invalid or duplicate scatter field.");
            }
        }
        return fields;
    }

    private static IReadOnlyDictionary<string, string> Fields(YamlNode node) =>
        new ReadOnlyDictionary<string, string>(Map(node).ToDictionary(pair => pair.Key, pair => Scalar(pair.Value), StringComparer.Ordinal));

    private static void RequireKeys(Dictionary<string, YamlNode> fields, string first, string second)
    {
        if (fields.Count != 2 || !fields.ContainsKey(first) || !fields.ContainsKey(second))
        {
            throw Invalid("Unsupported scatter container fields.");
        }
    }

    private static string Scalar(YamlNode node) =>
        node is YamlScalarNode { Value: { } value } && value.Length <= 4096
            ? value : throw Invalid("Expected a bounded scalar scatter field.");

    private static string Required(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw Invalid($"Missing scatter field: {key}.");

    private static bool Boolean(IReadOnlyDictionary<string, string> fields, string key) =>
        bool.TryParse(Required(fields, key), out var value) ? value : throw Invalid($"Invalid scatter boolean: {key}.");

    private static ulong Number(IReadOnlyDictionary<string, string> fields, string key)
    {
        var text = Required(fields, key).AsSpan();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out var value))
        {
            throw Invalid($"Invalid scatter unsigned number: {key}.");
        }
        return value;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class BoundedParser(IParser inner, CancellationToken cancellationToken) : IParser
    {
        private int _depth;
        private int _events;
        public ParsingEvent? Current => inner.Current;

        public bool MoveNext()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inner.MoveNext())
            {
                return false;
            }
            if (++_events > 131072 || Current is AnchorAlias
                || Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)
                || Current is Scalar scalar && scalar.Value.Length > 4096)
            {
                throw Invalid("Scatter aliases, tags, anchors or excessive YAML events/scalars are unsupported.");
            }
            if (Current is MappingStart or SequenceStart)
            {
                if (++_depth > 10)
                {
                    throw Invalid("Scatter nesting exceeds its supported structure.");
                }
            }
            else if (Current is MappingEnd or SequenceEnd)
            {
                _depth--;
            }
            return true;
        }
    }
}