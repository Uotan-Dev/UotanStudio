namespace UotanStudio.Services.Patching;

/// <summary>Immutable bounded configuration requests, shared safely by branches of a node graph.<para>不可变的有界配置请求集合，可在节点图的各分支间安全共享。</para></summary>
public sealed class PatchImageEdits
{
    private readonly PatchImageTextEdit[] _items;
    private readonly long _characters;
    private PatchImageEdits(PatchImageTextEdit[] items, long characters) { _items = items; _characters = characters; }
    /// <summary>An empty request collection.<para>空的请求集合。</para></summary>
    /// <value>The shared empty instance.<para>共享的空实例。</para></value>
    public static PatchImageEdits Empty { get; } = new([], 0);
    /// <summary>Read-only view of the accepted configuration requests.<para>已接受配置请求的只读视图。</para></summary>
    /// <value>The requests in insertion order.<para>按插入顺序排列的请求。</para></value>
    public IReadOnlyList<PatchImageTextEdit> Items => Array.AsReadOnly(_items);

    /// <summary>Returns an independent collection, rejecting duplicate targets and excessive retained text.<para>返回独立的新集合，拒绝重复目标与超出预算的文本。</para></summary>
    /// <param name="edit">The request to append.<para>要追加的请求。</para></param>
    /// <returns>A new collection containing the appended request.<para>包含所追加请求的新集合。</para></returns>
    public PatchImageEdits Append(PatchImageTextEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.Path);
        ArgumentNullException.ThrowIfNull(edit.Text);
        var path = edit.Path.TrimStart('/');
        if (edit.Path.Length > 4096 || path.Split('/').Any(part => part is "" or "." or "..")
            || path.Contains('\\') || path.Contains('\0') || _items.Any(item => item.Path.TrimStart('/') == path))
        {
            throw new InvalidDataException("Image edit paths must be valid and distinct.");
        }
        var characters = _characters + edit.Text.Length;
        if (_items.Length >= 256 || edit.Text.Length > 4 * 1024 * 1024 || characters > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Image edit collection exceeds its count or text budget.");
        }
        return new([.. _items, edit], characters);
    }
}