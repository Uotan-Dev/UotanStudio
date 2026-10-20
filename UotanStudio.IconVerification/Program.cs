using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace UotanStudio.IconVerification;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var outputRoot = Path.Combine(repositoryRoot, "artifacts", "icon-verification");
            var expectedSuffix = Path.Combine("artifacts", "icon-verification");
            if (!outputRoot.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected verification output path '{outputRoot}'.");
            }

            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            Directory.CreateDirectory(outputRoot);
            var iconDirectory = Path.Combine(outputRoot, "icons");
            var sheetDirectory = Path.Combine(outputRoot, "sheets");

            AppBuilder.Configure<VerificationApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var catalog = IconCatalog.Create();
            var targets = catalog.Discover(repositoryRoot, out var missing);
            Console.WriteLine($"Discovered {targets.Count} icon targets ({targets.Count(target => !target.IsAppIcon)} static, " +
                              $"{targets.Count(target => target.IsAppIcon)} AppIcon).");
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"Missing TDesign types: {string.Join(", ", missing)}");
            }

            var renderer = new IconRenderer(catalog, iconDirectory);
            var runner = new VerificationRunner(renderer, sheetDirectory);
            return runner.Run(targets, missing, repositoryRoot, outputRoot);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "UotanStudio.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("No directory containing UotanStudio.slnx was found.");
    }

    private sealed class VerificationApplication : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}