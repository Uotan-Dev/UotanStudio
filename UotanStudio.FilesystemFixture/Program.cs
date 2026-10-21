using UotanStudio.FilesystemFixture;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 2;
}

if (!string.Equals(args[0], "roundtrip", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
    PrintUsage();
    return 2;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 1; index < args.Length; index++)
{
    var argument = args[index];
    if (!argument.StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Unexpected argument: {argument}");
        return 2;
    }

    if (index + 1 >= args.Length)
    {
        Console.Error.WriteLine($"Missing value for {argument}");
        return 2;
    }

    options[argument[2..]] = args[++index];
}

foreach (var required in new[] { "format", "source", "work", "repacked", "manifest", "report" })
{
    if (!options.ContainsKey(required))
    {
        Console.Error.WriteLine($"Missing --{required}");
        PrintUsage();
        return 2;
    }
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return await RoundTripRunner.RunAsync(
        options["format"],
        options["source"],
        options["work"],
        options["repacked"],
        options["manifest"],
        options["report"],
        options.TryGetValue("allow-fresh", out var allowFresh)
            && bool.TryParse(allowFresh, out var parsed)
            && parsed,
        cancellation.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          UotanStudio.FilesystemFixture roundtrip \
            --format EXT4|EROFS|F2FS \
            --source <image> \
            --work <directory> \
            --repacked <image> \
            --manifest <tsv> \
            --report <json> \
            [--allow-fresh true|false]
        """);
}