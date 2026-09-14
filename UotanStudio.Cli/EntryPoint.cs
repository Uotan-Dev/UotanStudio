namespace UotanStudio.Cli;

internal static class EntryPoint
{
    public static int Main(string[] args)
        => CliProgram.RunAsync(CliProgram.StripModeSwitch(args)).GetAwaiter().GetResult();
}