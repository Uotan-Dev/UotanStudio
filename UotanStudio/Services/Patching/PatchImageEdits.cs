namespace UotanStudio.Services.Patching;

/// <summary>Immutable bounded configuration requests, shared safely by branches of a node graph.</summary>
public sealed class PatchImageEdits
{
    private readonly PatchImageTextEdit[] _items;
    private readonly long _characters;
    private PatchImageEdits(PatchImageTextEdit[] items, long characters) { _items = items; _characters = characters; }
    /// <summary>An empty request collection.</summary>
    public static PatchImageEdits Empty { get; } = new([], 0);
    /// <summary>Read-only requests consumed by the batch staging adapter.</summary>
    public IReadOnlyList<PatchImageTextEdit> Items => Array.AsReadOnly(_items);

    /// <summary>Returns an independent collection, rejecting duplicate targets and excessive retained text.</summary>
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