using System.Diagnostics;

namespace UotanStudio.Views.Editors;

internal static class MirrorTrace
{
    private static readonly Lock Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string FilePath =
        Path.Combine(Path.GetTempPath(), "uotan-mirror-trace.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"{Clock.ElapsedMilliseconds,8} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}