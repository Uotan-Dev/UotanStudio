namespace UotanStudio.Cli;

internal static class EntryPoint
{
    /// <summary>
    /// Process entry point that dispatches arguments to the shared CLI command tree.
    /// <para>进程入口，将参数分发给共享的 CLI 命令树。</para>
    /// </summary>
    public static int Main(string[] args)
        => CliProgram.RunAsync(CliProgram.StripModeSwitch(args)).GetAwaiter().GetResult();
}