using Avalonia.Controls;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Indexes bundled node labels in both languages without changing the active application locale.<para>在不改变当前应用程序区域设置的情况下，为两种语言的内置节点标签建立索引。</para></summary>
internal static class NodeSearchIndex
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> Labels = new(LoadLabels);

    internal static IReadOnlyList<string> GetLabels(string resourceKey)
        => Labels.Value.TryGetValue(resourceKey, out var labels) ? labels : [];

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadLabels()
    {
        var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        ResourceDictionary[] dictionaries = [new Locale.EnUs.Node(), new Locale.ZhCn.Node(), new Locale.EnUs.Interaction(), new Locale.ZhCn.Interaction()];
        foreach (var dictionary in dictionaries)
        {
            foreach (var entry in dictionary)
            {
                if (entry.Key is not string key || entry.Value is not string value)
                {
                    continue;
                }
                if (!labels.TryGetValue(key, out var values))
                {
                    values = [];
                    labels[key] = values;
                }
                values.Add(value);
            }
        }
        return labels.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value], StringComparer.Ordinal);
    }
}