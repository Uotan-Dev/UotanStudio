using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UotanStudio.ViewModels;

namespace UotanStudio.IconVerification;

internal sealed class VerificationRunner
{
    private static readonly Color White = Colors.White;
    private static readonly Color DarkBackground = Color.Parse("#202020");
    private static readonly Color DarkIcon = Color.Parse("#202020");
    private static readonly Color SheetAccent = Color.Parse("#0B5FFF");

    private readonly IconRenderer _renderer;
    private readonly string _sheetDirectory;
    private readonly List<string> _failures = [];
    private readonly List<string> _report = [];

    public VerificationRunner(IconRenderer renderer, string sheetDirectory)
    {
        _renderer = renderer;
        _sheetDirectory = sheetDirectory;
        Directory.CreateDirectory(sheetDirectory);
    }

    public int Run(
        IReadOnlyList<IconTarget> targets,
        IReadOnlyList<string> missing,
        string repositoryRoot,
        string outputRoot)
    {
        _report.Add("IRIHI Iconica TDesign icon verification");
        _report.Add($"Repository: {repositoryRoot}");
        _report.Add($"Targets: {targets.Count} ({targets.Count(target => !target.IsAppIcon)} static, " +
                    $"{targets.Count(target => target.IsAppIcon)} AppIcon)");
        _report.Add($"Output: {outputRoot}");
        _report.Add(string.Empty);

        foreach (var name in missing)
        {
            Fail($"XAML references unknown TDesign icon class '{name}'.");
        }

        VerifyAllTargets(targets);
        VerifySpacing(targets);
        VerifySizes(targets);
        VerifyDarkForeground(targets);
        RenderContactSheets(targets);
        RenderForegroundSheet(targets);

        _report.Add(string.Empty);
        _report.Add($"Failures: {_failures.Count}");
        foreach (var failure in _failures)
        {
            _report.Add($"  {failure}");
        }

        File.WriteAllLines(Path.Combine(outputRoot, "report.txt"), _report);
        var summary = new[]
        {
            $"Targets: {targets.Count}",
            $"Static: {targets.Count(target => !target.IsAppIcon)}",
            $"AppIcon: {targets.Count(target => target.IsAppIcon)}",
            $"Failures: {_failures.Count}",
            $"Icons: {Path.Combine(outputRoot, "icons")}",
            $"Sheets: {_sheetDirectory}",
        };
        File.WriteAllLines(Path.Combine(outputRoot, "summary.txt"), summary);

        Console.WriteLine($"Verified {targets.Count} icon targets.");
        Console.WriteLine($"Failures: {_failures.Count}");
        Console.WriteLine($"Report: {Path.Combine(outputRoot, "report.txt")}");
        Console.WriteLine($"Sheets: {_sheetDirectory}");
        Console.WriteLine($"Icon PNGs: {Path.Combine(outputRoot, "icons")}");
        return _failures.Count == 0 ? 0 : 1;
    }

    private void VerifyAllTargets(IReadOnlyList<IconTarget> targets)
    {
        var index = 0;
        foreach (var target in targets)
        {
            var red = index % 2 == 0;
            var foreground = red ? Colors.Red : Colors.Blue;
            var expectation = red ? ForegroundExpectation.Red : ForegroundExpectation.Blue;
            var (analysis, _) = _renderer.RenderAndAnalyze(target, 96, White, foreground, expectation);
            CheckTarget(target, analysis, expectation);
            index++;
        }
    }

    private void VerifySpacing(IReadOnlyList<IconTarget> targets)
    {
        var preferred = new[]
        {
            "File", "Folder", "Search", "PlayCircleStroke", "Pin", "Minus", "Fullscreen", "Thunder",
            "ViewColumn", "Cpu",
        };
        var spacingTargets = preferred
            .Select(name => targets.FirstOrDefault(target => !target.IsAppIcon && target.TypeName == name))
            .OfType<IconTarget>()
            .ToList();
        if (spacingTargets.Count < 2)
        {
            spacingTargets = targets.Where(target => !target.IsAppIcon).Take(10).ToList();
        }

        const int iconSize = 48;
        const int gap = 24;
        const int margin = 24;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = gap,
            Margin = new Thickness(margin),
        };
        foreach (var target in spacingTargets)
        {
            panel.Children.Add(_renderer.CreateIcon(target, iconSize, Colors.Red));
        }

        var width = margin * 2 + spacingTargets.Count * iconSize + (spacingTargets.Count - 1) * gap;
        var height = margin * 2 + iconSize;
        var frame = IconRenderer.RenderContent(
            new Border { Width = width, Height = height, Background = new SolidColorBrush(White), Child = panel },
            width,
            height,
            White);
        frame.Save(Path.Combine(_sheetDirectory, "spacing.png"), new PngBitmapEncoderOptions());

        if (IconRenderer.CountPainted(frame, White, new PixelRect(0, 0, margin, height)) != 0
            || IconRenderer.CountPainted(frame, White, new PixelRect(width - margin, 0, margin, height)) != 0
            || IconRenderer.CountPainted(frame, White, new PixelRect(0, 0, width, margin)) != 0
            || IconRenderer.CountPainted(frame, White, new PixelRect(0, height - margin, width, margin)) != 0)
        {
            Fail("Spacing row paints inside its outer margin.");
        }

        for (var i = 0; i < spacingTargets.Count; i++)
        {
            var x = margin + i * (iconSize + gap);
            var painted = IconRenderer.CountPainted(frame, White, new PixelRect(x, margin, iconSize, iconSize));
            if (painted == 0)
            {
                Fail($"Spacing row icon '{spacingTargets[i].DisplayName}' is empty in its cell.");
            }

            if (IconRenderer.CountPainted(frame, White, new PixelRect(x + iconSize, margin, gap, iconSize)) != 0)
            {
                Fail($"Spacing row icon '{spacingTargets[i].DisplayName}' paints into the 24 px gap after it.");
            }
        }

        Pass($"spacing: {spacingTargets.Count} icons at 48 px, 24 px gaps, 24 px outer margin");
    }

    private void VerifySizes(IReadOnlyList<IconTarget> targets)
    {
        var search = targets.FirstOrDefault(target => !target.IsAppIcon && target.TypeName == "Search")
            ?? targets.FirstOrDefault(target => !target.IsAppIcon);
        if (search is null)
        {
            Fail("No static icon is available for the size-scaling check.");
            return;
        }

        var measurements = new List<(int Size, IconAnalysis Analysis)>();
        foreach (var size in new[] { 16, 24, 48, 96 })
        {
            var (analysis, _) = _renderer.RenderAndAnalyze(
                search, size, White, Colors.Red, ForegroundExpectation.Red, suffix: $"at{size}");
            CheckTarget(search, analysis, ForegroundExpectation.Red, $"size {size}");
            measurements.Add((size, analysis));
        }

        var width24 = measurements.Single(item => item.Size == 24).Analysis.Bounds.Width;
        var width48 = measurements.Single(item => item.Size == 48).Analysis.Bounds.Width;
        var width96 = measurements.Single(item => item.Size == 96).Analysis.Bounds.Width;
        if (!IsScale(width24, width48, 1.5, 2.8) || !IsScale(width48, width96, 1.7, 2.3))
        {
            Fail($"Size scaling is not proportional: widths 24={width24}, 48={width48}, 96={width96}.");
        }

        RenderSizeSheet(search);
        Pass($"sizes: {search.DisplayName} at 16/24/48/96 px keeps proportional bounds");
    }

    private void VerifyDarkForeground(IReadOnlyList<IconTarget> targets)
    {
        var search = targets.FirstOrDefault(target => !target.IsAppIcon && target.TypeName == "Search");
        var appIcon = targets.FirstOrDefault(target => target.IsAppIcon && target.TypeName == nameof(AppIconKind.Pin))
            ?? targets.FirstOrDefault(target => target.IsAppIcon);
        if (search is null || appIcon is null)
        {
            Fail("Dark-background foreground check needs one static icon and one AppIcon.");
            return;
        }

        var (staticAnalysis, _) = _renderer.RenderAndAnalyze(
            search, 96, DarkBackground, Colors.White, ForegroundExpectation.White, suffix: "dark");
        CheckTarget(search, staticAnalysis, ForegroundExpectation.White, "dark background");

        var (appAnalysis, _) = _renderer.RenderAndAnalyze(
            appIcon, 96, DarkBackground, Colors.White, ForegroundExpectation.White, suffix: "dark");
        CheckTarget(appIcon, appAnalysis, ForegroundExpectation.White, "dark background AppIcon");
    }

    private void RenderContactSheets(IReadOnlyList<IconTarget> targets)
    {
        RenderSheets(targets.Where(target => !target.IsAppIcon).ToList(), "static", DarkIcon);
        RenderSheets(targets.Where(target => target.IsAppIcon).ToList(), "appicon", SheetAccent);
    }

    private void RenderSheets(IReadOnlyList<IconTarget> targets, string prefix, Color foreground)
    {
        const int columns = 5;
        const int cellWidth = 170;
        const int cellHeight = 150;
        const int iconSize = 96;
        for (var start = 0; start < targets.Count; start += columns * 5)
        {
            var page = targets.Skip(start).Take(columns * 5).ToList();
            var rows = (int)Math.Ceiling(page.Count / (double)columns);
            var width = columns * cellWidth;
            var height = rows * cellHeight;
            var panel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = cellWidth,
                ItemHeight = cellHeight,
                Width = width,
                Height = height,
            };
            for (var index = 0; index < page.Count; index++)
            {
                panel.Children.Add(BuildCell(page[index], cellWidth, cellHeight, iconSize, foreground, index % 2 == 0));
            }

            var border = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(White),
                Child = panel,
            };
            var pageNumber = start / (columns * 5) + 1;
            _renderer.RenderSheet(border, width, height, Path.Combine(_sheetDirectory, $"{prefix}-{pageNumber:00}.png"));
        }
    }

    private Control BuildCell(
        IconTarget target,
        int cellWidth,
        int cellHeight,
        int iconSize,
        Color foreground,
        bool alternate)
    {
        var icon = _renderer.CreateIcon(target, iconSize, foreground);
        var label = new TextBlock
        {
            Text = target.DisplayName,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = cellWidth - 12,
            Foreground = new SolidColorBrush(Color.Parse("#202020")),
        };
        var stack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, label },
        };
        return new Border
        {
            Width = cellWidth,
            Height = cellHeight,
            Background = new SolidColorBrush(alternate ? Color.Parse("#F2F2F2") : White),
            Child = stack,
        };
    }

    private void RenderSizeSheet(IconTarget search)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        foreach (var size in new[] { 16, 24, 48, 96 })
        {
            var column = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Bottom,
                Children =
                {
                    _renderer.CreateIcon(search, size, DarkIcon),
                    new TextBlock
                    {
                        Text = $"{size} px",
                        FontSize = 11,
                        TextAlignment = TextAlignment.Center,
                        Foreground = new SolidColorBrush(DarkIcon),
                    },
                },
            };
            row.Children.Add(column);
        }

        const int width = 320;
        const int height = 160;
        var border = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(White),
            Child = row,
        };
        _renderer.RenderSheet(border, width, height, Path.Combine(_sheetDirectory, "sizes.png"));
    }

    private void RenderForegroundSheet(IReadOnlyList<IconTarget> targets)
    {
        var preferred = new[] { "File", "Folder", "Search", "Pin" };
        var icons = preferred
            .Select(name => targets.FirstOrDefault(target => !target.IsAppIcon && target.TypeName == name))
            .OfType<IconTarget>()
            .ToList();
        if (icons.Count == 0)
        {
            icons = targets.Where(target => !target.IsAppIcon).Take(4).ToList();
        }

        var rows = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        AddForegroundRow(rows, icons, "Red", Color.Parse("#D93025"), White);
        AddForegroundRow(rows, icons, "Blue", Color.Parse("#1A73E8"), White);
        AddForegroundRow(rows, icons, "Green", Color.Parse("#188038"), White);
        AddForegroundRow(rows, icons, "White / dark", Colors.White, DarkBackground);

        const int width = 520;
        const int height = 552;
        var border = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(White),
            Child = rows,
        };
        _renderer.RenderSheet(border, width, height, Path.Combine(_sheetDirectory, "foreground.png"));
    }

    private void AddForegroundRow(StackPanel rows, IReadOnlyList<IconTarget> icons, string label, Color foreground, Color background)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    Width = 90,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(background == White ? DarkIcon : White),
                },
            },
        };
        foreach (var icon in icons)
        {
            row.Children.Add(_renderer.CreateIcon(icon, 48, foreground));
        }

        rows.Children.Add(new Border
        {
            Background = new SolidColorBrush(background),
            Padding = new Thickness(6),
            Child = row,
        });
    }

    private void CheckTarget(
        IconTarget target,
        IconAnalysis analysis,
        ForegroundExpectation expectation,
        string? scenario = null)
    {
        var issues = new List<string>();
        if (analysis.PaintedPixels < 10)
        {
            issues.Add($"painted={analysis.PaintedPixels}");
        }

        if (analysis.PaintedPixels > 0)
        {
            var bounds = analysis.Bounds;
            if (bounds.Left < 0 || bounds.Top < 0
                || bounds.Right >= analysis.Size || bounds.Bottom >= analysis.Size)
            {
                issues.Add($"bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}");
            }

            if (bounds.Width < 4 || bounds.Height < 4)
            {
                issues.Add($"tiny={bounds.Width}x{bounds.Height}");
            }
        }

        var minimumDominantRatio = expectation == ForegroundExpectation.White ? 0.70 : 0.80;
        if (analysis.DominantRatio < minimumDominantRatio)
        {
            issues.Add($"foreground={analysis.DominantRatio:P1} < {minimumDominantRatio:P0}");
        }

        var name = scenario is null ? target.DisplayName : $"{target.DisplayName} ({scenario})";
        var edge = analysis.TouchesEdge ? ", edge=yes" : string.Empty;
        var line = $"{name}: painted={analysis.PaintedPixels}, " +
                   $"bbox={analysis.Bounds.Left},{analysis.Bounds.Top},{analysis.Bounds.Right},{analysis.Bounds.Bottom}, " +
                   $"foreground={analysis.DominantRatio:P1}{edge}";
        if (issues.Count == 0)
        {
            Pass(line);
            return;
        }

        Fail($"{line}; {string.Join("; ", issues)}");
    }

    private static bool IsScale(int smaller, int larger, double min, double max)
    {
        if (smaller <= 0)
        {
            return false;
        }

        var ratio = (double)larger / smaller;
        return ratio >= min && ratio <= max;
    }

    private void Pass(string message) => _report.Add($"[PASS] {message}");

    private void Fail(string message)
    {
        _failures.Add(message);
        _report.Add($"[FAIL] {message}");
        Console.Error.WriteLine($"[FAIL] {message}");
    }
}