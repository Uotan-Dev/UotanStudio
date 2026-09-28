using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Queries and edits strict JSON with RFC 6901 pointers and bounded document complexity.
/// <para>使用 RFC 6901 指针查询和编辑严格 JSON，并对文档复杂度设有上限。</para>
/// </summary>
public static class JsonPatch
{
    private const int MaxNodes = 100_000;
    private const int MaxDepth = 64;

    /// <summary>
    /// Validates a strict JSON document without serializing it.
    /// <para>校验严格的 JSON 文档，但不将其序列化输出。</para>
    /// </summary>
    /// <param name="text">
    /// JSON text subject to the shared limits: at most <see cref="TextPatch.MaxCharacters"/> UTF-16 code units,
    /// 100000 nodes, depth 64, and no duplicate object keys.
    /// <para>受共享上限约束的 JSON 文本：最多 <see cref="TextPatch.MaxCharacters"/> 个 UTF-16 代码单元、
    /// 100000 个节点、深度 64，且对象键不得重复。</para>
    /// </param>
    /// <param name="cancellationToken">Checked before parsing and while walking each node.
    /// <para>在解析之前以及遍历每个节点时都会检查。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="JsonException">The text is not strict JSON.
    /// <para>文本不是严格的 JSON。</para></exception>
    /// <exception cref="InvalidDataException">A size, node-count, depth or duplicate-key limit is exceeded.
    /// <para>超出大小、节点数量、深度或重复键限制。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已发出取消信号。</para></exception>
    public static void Validate(string text, CancellationToken cancellationToken = default)
        => Parse(text, cancellationToken);

    /// <summary>
    /// Returns serialized JSON, including literal null, or null when the selected member is absent.
    /// <para>返回序列化后的 JSON（包含字面 null）；所选成员不存在时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <example><code>string? value = JsonPatch.Read(document, "/vendor/props/persist", token);</code></example>
    /// <param name="text">Strict JSON document bounded by the same limits as <see cref="Validate(string, CancellationToken)"/>.
    /// <para>受与 <see cref="Validate(string, CancellationToken)"/> 相同上限约束的严格 JSON 文档。</para></param>
    /// <param name="pointer">
    /// RFC 6901 pointer. An empty string selects the root; otherwise the pointer must begin with <c>/</c>, stay within
    /// 16384 characters and 64 segments, and use only the <c>~0</c> and <c>~1</c> escapes. Array segments are
    /// nonnegative decimal indexes without leading zeros.
    /// <para>RFC 6901 指针。空字符串选中根节点；否则必须以 <c>/</c> 开头、不超过 16384 个字符和 64 段，
    /// 并且只使用 <c>~0</c> 与 <c>~1</c> 转义。数组段必须是不带前导零的非负十进制索引。</para>
    /// </param>
    /// <param name="cancellationToken">Checked before parsing, while walking nodes and while serializing.
    /// <para>在解析之前、遍历节点时以及序列化过程中都会检查。</para></param>
    /// <returns>
    /// The selected node serialized as compact JSON text; a node holding JSON <c>null</c> yields the text <c>null</c>.
    /// <see langword="null"/> when some pointer segment does not resolve, including an array index beyond the element count.
    /// <para>所选节点序列化后的紧凑 JSON 文本；值为 JSON <c>null</c> 的节点返回文本 <c>null</c>。
    /// 当某个指针段无法解析时返回 <see langword="null"/>，包括超出元素数量的数组索引。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="pointer"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 或 <paramref name="pointer"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="JsonException"><paramref name="text"/> is not strict JSON.
    /// <para><paramref name="text"/> 不是严格的 JSON。</para></exception>
    /// <exception cref="InvalidDataException">The pointer, its length, its escapes or an array index is invalid, or a document limit is exceeded.
    /// <para>指针本身、其长度、其转义或数组索引无效，或超出文档上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已发出取消信号。</para></exception>
    public static string? Read(string text, string pointer, CancellationToken cancellationToken = default)
    {
        var root = Parse(text, cancellationToken);
        var (exists, value) = Resolve(root, Segments(pointer));
        return exists ? Serialize(value, cancellationToken) : null;
    }

    /// <summary>
    /// Sets or deletes one value. Creation requires an existing object parent; arrays never grow implicitly.
    /// <para>设置或删除一个值。新建条目要求其父节点是已存在的对象；数组绝不会隐式增长。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// JsonPatch.Apply(document, "/system/enforcing", "false", false, true, "true", false, token);
    /// </code>
    /// </example>
    /// <param name="text">Strict JSON document bounded by the same limits as <see cref="Validate(string, CancellationToken)"/>.
    /// <para>受与 <see cref="Validate(string, CancellationToken)"/> 相同上限约束的严格 JSON 文档。</para></param>
    /// <param name="pointer">RFC 6901 pointer validated by the same rules as in <see cref="Read"/>; the empty string addresses the root.
    /// <para>RFC 6901 指针，校验规则与 <see cref="Read"/> 相同；空字符串表示根节点。</para></param>
    /// <param name="value">Replacement JSON text parsed under the same document limits; ignored when <paramref name="delete"/> is set.
    /// <para>用于替换的 JSON 文本，按相同的文档上限解析；<paramref name="delete"/> 置位时被忽略。</para></param>
    /// <param name="delete">Removes the selected member instead of assigning <paramref name="value"/>.
    /// <para>删除所选成员，而不是赋值为 <paramref name="value"/>。</para></param>
    /// <param name="checkOldValue">Requires the current value to equal <paramref name="expectedOldValue"/>; also mandatory for deleting array elements.
    /// <para>要求当前值等于 <paramref name="expectedOldValue"/>；删除数组元素时同样必须启用。</para></param>
    /// <param name="expectedOldValue">JSON text compared deeply against the current value; parsed only when <paramref name="checkOldValue"/> is set.
    /// <para>与当前值进行深度比较的 JSON 文本；仅在 <paramref name="checkOldValue"/> 置位时才解析。</para></param>
    /// <param name="allowCreate">Permits assigning a missing member under an existing object parent or replacing the root.
    /// <para>允许在已存在的对象父节点下赋值新成员，或替换根节点。</para></param>
    /// <param name="cancellationToken">Checked before each parse, node walk and serialization step.
    /// <para>在每次解析、节点遍历和序列化步骤之前都会检查。</para></param>
    /// <returns>
    /// The re-parsed output text, the number of pre-existing targets (the root or an existing member counts as one, a created
    /// or delete-missed member counts as zero) and whether the emitted text differs from <paramref name="text"/>.
    /// <para>重新解析后的输出文本、已存在目标的数量（根节点或已存在的成员计为一个，新建或未命中删除计为零）
    /// 以及输出的文本是否与 <paramref name="text"/> 不同。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="pointer"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 或 <paramref name="pointer"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="JsonException">Any supplied JSON text is not strict JSON.
    /// <para>任一传入的 JSON 文本不是严格 JSON。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The old-value guard fails, the root is deleted, a missing member is edited with creation disabled, the pointer parent
    /// is missing or neither object nor array, an array element is deleted without an expected old value, an array index is
    /// out of range, or a document limit is exceeded.
    /// <para>旧值守卫校验失败、删除根节点、在禁用新建时编辑缺失成员、指针父节点缺失或既非对象也非数组、
    /// 未在删除数组元素时提供期望旧值、数组索引越界，或超出文档上限。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已发出取消信号。</para></exception>
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