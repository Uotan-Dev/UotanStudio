using UotanStudio.Services;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Indexes bundled node labels in both languages without changing the active application locale.<para>在不改变当前应用程序区域设置的情况下，为两种语言的内置节点标签建立索引。</para></summary>
internal static class NodeSearchIndex
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> Labels = new(LoadLabels);

    internal static IReadOnlyList<string> GetLabels(string resourceKey)
        => Labels.Value.TryGetValue(resourceKey, out var labels) ? labels : [];

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadLabels()
    {
        var labels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var key in Locale.LocaleKeySets.Node
                     .Concat(Locale.LocaleKeySets.Interaction))
        {
            var values = LanguageService.Instance.GetAllTranslations(key);
            if (values.Count > 0)
            {
                labels[key] = values;
            }
        }

        return labels;
    }
}