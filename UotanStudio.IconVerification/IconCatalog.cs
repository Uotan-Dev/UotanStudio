using System.Text.RegularExpressions;
using Irihi.Iconica.TDesign;
using UotanStudio.ViewModels;

namespace UotanStudio.IconVerification;

internal enum IconTargetKind
{
    Static,
    AppIcon,
}

internal sealed record IconTarget(string DisplayName, string TypeName, IconTargetKind Kind)
{
    public bool IsAppIcon => Kind == IconTargetKind.AppIcon;
}

internal sealed partial class IconCatalog
{
    private readonly Dictionary<string, Type> _types;

    private IconCatalog(Dictionary<string, Type> types) => _types = types;

    public static IconCatalog Create()
    {
        var assembly = typeof(TDesignIconBase).Assembly;
        var types = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && string.Equals(type.Namespace, "Irihi.Iconica.TDesign.Icons", StringComparison.Ordinal)
                && typeof(TDesignIconBase).IsAssignableFrom(type))
            .ToDictionary(type => type.Name, StringComparer.Ordinal);
        return new IconCatalog(types);
    }

    public IReadOnlyList<IconTarget> Discover(string repositoryRoot, out IReadOnlyList<string> missing)
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "UotanStudio"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var prefixes = NamespacePattern().Matches(text)
                .Select(match => match.Groups["prefix"].Value)
                .ToHashSet(StringComparer.Ordinal);
            if (prefixes.Count == 0)
            {
                continue;
            }

            foreach (Match match in TagPattern().Matches(text))
            {
                if (prefixes.Contains(match.Groups["prefix"].Value))
                {
                    used.Add(match.Groups["name"].Value);
                }
            }
        }

        var unknown = used.Where(name => !_types.ContainsKey(name)).ToArray();
        var targets = used.Where(_types.ContainsKey)
            .Select(name => new IconTarget(name, name, IconTargetKind.Static))
            .ToList();

        foreach (var kind in Enum.GetValues<AppIconKind>())
        {
            targets.Add(new IconTarget($"AppIcon.{kind}", kind.ToString(), IconTargetKind.AppIcon));
        }

        missing = unknown;
        return targets;
    }

    public TDesignIconBase Create(string typeName)
    {
        if (!_types.TryGetValue(typeName, out var type))
        {
            throw new InvalidOperationException($"Unknown TDesign icon type '{typeName}'.");
        }

        return (TDesignIconBase)Activator.CreateInstance(type)!;
    }

    [GeneratedRegex("xmlns:(?<prefix>[A-Za-z_][A-Za-z0-9_]*)=\"https://irihi\\.tech/iconica/tdesign\"")]
    private static partial Regex NamespacePattern();

    [GeneratedRegex("<(?<prefix>[A-Za-z_][A-Za-z0-9_]*):(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TagPattern();
}