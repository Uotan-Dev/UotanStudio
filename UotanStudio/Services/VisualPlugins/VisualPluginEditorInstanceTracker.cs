namespace UotanStudio.Services.VisualPlugins;

internal sealed class VisualPluginEditorInstanceTracker
{
    private readonly Dictionary<string, object> _instances = new(StringComparer.Ordinal);

    public bool TryGet(string key, out object? instance) => _instances.TryGetValue(key, out instance);

    public bool TryRegister(string key, object instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(instance);
        return _instances.TryAdd(key, instance);
    }

    public void Remove(string key, object instance)
    {
        if (_instances.TryGetValue(key, out var current) && ReferenceEquals(current, instance))
        {
            _instances.Remove(key);
        }
    }
}