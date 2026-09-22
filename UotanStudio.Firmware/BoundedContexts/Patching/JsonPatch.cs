using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Queries and edits strict JSON with RFC 6901 pointers and bounded document complexity.</summary>
public static class JsonPatch
{
    private const int MaxNodes = 100_000;
    private const int MaxDepth = 64;

    /// <summary>Validates a strict JSON document without serializing it.</summary>
    public static void Validate(string text, CancellationToken cancellationToken = default)
        => Parse(text, cancellationToken);

    /// <summary>Returns serialized JSON, including literal null, or null when the selected member is absent.</summary>
    public static string? Read(string text, string pointer, CancellationToken cancellationToken = default)
    {
        var root = Parse(text, cancellationToken);
        var (exists, value) = Resolve(root, Segments(pointer));
        return exists ? Serialize(value, cancellationToken) : null;
    }

    /// <summary>Sets or deletes one value. Creation requires an existing object parent; arrays never grow implicitly.</summary>
    public static TextPatchResult Apply(string text, string pointer, string value, bool delete,
        bool checkOldValue, string expectedOldValue, bool allowCreate, CancellationToken cancellationToken = default)
    {
        var root = Parse(text, cancellationToken);
        var segments = Segments(pointer);
        var (exists, current) = Resolve(root, segments);
        var replacement = delete ? null : Parse(value, cancellationToken);
        var expected = checkOldValue ? Parse(expectedOldValue, cancellationToken) : null;
        if (exists && !delete && JsonNode.DeepEquals(current, replacement))
        {
            return new TextPatchResult(text, 1, false);
        }
        if (checkOldValue && (!exists || !JsonNode.DeepEquals(current, expected)))
        {
            throw new InvalidDataException("The JSON value does not match the expected old value.");
        }
        if (delete && segments.Length == 0)
        {
            throw new InvalidDataException("Deleting the JSON document root is not supported.");
        }
        if (!exists && delete)
        {
            return new TextPatchResult(text, 0, false);
        }
        if (!exists && !allowCreate)
        {
            throw new InvalidDataException("The JSON value is missing; creation is disabled.");
        }
        if (segments.Length == 0)
        {
            root = replacement;
        }
        else
        {
            var (parentExists, parent) = Resolve(root, segments[..^1]);
            if (!parentExists || parent is not JsonObject && parent is not JsonArray)
            {
                throw new InvalidDataException("The JSON pointer parent must be an existing object or array.");
            }
            var key = segments[^1];
            if (parent is JsonObject obj)
            {
                if (delete)
                {
                    obj.Remove(key);
                }
                else
                {
                    obj[key] = replacement;
                }
            }
            else if (parent is JsonArray array)
            {
                // Removing an element shifts later indices; never do it without an explicit precondition.
                if (delete && !checkOldValue)
                {
                    throw new InvalidDataException("Deleting a JSON array element requires an expected old value.");
                }
                var index = ArrayIndex(key);
                if (index >= array.Count)
                {
                    throw new InvalidDataException("JSON array edits require an existing index.");
                }
                if (delete)
                {
                    array.RemoveAt(index);
                }
                else
                {
                    array[index] = replacement;
                }
            }
        }
        var output = Serialize(root, cancellationToken);
        // Check combined depth and node count as well as each individual input.
        Parse(output, cancellationToken);
        return new TextPatchResult(output, exists ? 1 : 0, true);
    }

    private static JsonNode? Parse(string text, CancellationToken token)
    {
        TextPatch.ValidateSize(text);
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth });
        var count = 0;
        Validate(document.RootElement, ref count, token);
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth });
    }

    private static void Validate(JsonElement element, ref int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (++count > MaxNodes)
        {
            throw new InvalidDataException("JSON exceeds the node limit.");
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate JSON object keys are ambiguous.");
                }
                Validate(property.Value, ref count, token);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                Validate(child, ref count, token);
            }
        }
    }

    private static string[] Segments(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length == 0)
        {
            return [];
        }
        if (pointer.Length > 16_384 || pointer[0] != '/')
        {
            throw new InvalidDataException("A JSON pointer must be empty or begin with '/', within the length limit.");
        }
        var segments = pointer[1..].Split('/');
        if (segments.Length > MaxDepth)
        {
            throw new InvalidDataException("JSON pointer exceeds the depth limit.");
        }
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            for (var offset = 0; offset < segment.Length; offset++)
            {
                if (segment[offset] == '~' && (++offset == segment.Length || segment[offset] is not '0' and not '1'))
                {
                    throw new InvalidDataException("Invalid JSON pointer escape; use ~0 or ~1.");
                }
            }
            segments[index] = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
        return segments;
    }

    private static (bool Exists, JsonNode? Value) Resolve(JsonNode? node, string[] segments)
    {
        foreach (var segment in segments)
        {
            if (node is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(segment, out node))
                {
                    return (false, null);
                }
            }
            else if (node is JsonArray array)
            {
                var index = ArrayIndex(segment);
                if (index >= array.Count)
                {
                    return (false, null);
                }
                node = array[index];
            }
            else
            {
                return (false, null);
            }
        }
        return (true, node);
    }

    private static int ArrayIndex(string segment)
    {
        if (segment.Length == 0 || segment.Length > 1 && segment[0] == '0'
            || !segment.All(char.IsAsciiDigit)
            || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new InvalidDataException("JSON array indices must be nonnegative decimal integers without leading zeros.");
        }
        return index;
    }

    private static string Serialize(JsonNode? node, CancellationToken token)
    {
        using var output = new LimitedJsonStream(token);
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = MaxDepth }))
        {
            if (node is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                node.WriteTo(writer);
            }
        }
        var text = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        TextPatch.ValidateSize(text);
        return text;
    }

    private sealed class LimitedJsonStream(CancellationToken token) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public byte[] GetBuffer() => _buffer.GetBuffer();
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (Length + buffer.Length > TextPatch.MaxCharacters * 4L)
            {
                throw new InvalidDataException("Serialized JSON exceeds the byte limit.");
            }
            _buffer.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}