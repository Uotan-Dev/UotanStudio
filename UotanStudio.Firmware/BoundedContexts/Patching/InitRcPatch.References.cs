namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Static evidence for named service-control references; not a complete Android init validation result.</summary>
public sealed record InitRcReferenceReport(
    IReadOnlyList<string> Services,
    IReadOnlyList<string> MissingServices,
    IReadOnlyList<string> DynamicReferences,
    IReadOnlyList<string> InvalidReferences,
    IReadOnlyList<string> AmbiguousServices,
    int References)
{
    /// <summary>Whether every inspected reference is statically resolved without malformed commands or ambiguous declarations.</summary>
    public bool ReferencesValid => MissingServices.Count == 0 && DynamicReferences.Count == 0
        && InvalidReferences.Count == 0 && AmbiguousServices.Count == 0;
}

public static partial class InitRcPatch
{
    /// <summary>Checks start/stop/restart/enable/exec_start in actions and service onrestart commands against explicit declarations.</summary>
    public static InitRcReferenceReport CheckServiceReferences(string text, string externalServices, CancellationToken token)
    {
        var sections = Parse(text, token);
        TextPatch.ValidateSize(externalServices);
        var services = new HashSet<string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections.Where(section => section.Header.Words[0] == "service"))
        {
            token.ThrowIfCancellationRequested();
            var name = section.Header.Words[1];
            if (!services.Add(name)) { ambiguous.Add(name); }
        }
        var external = externalServices.Split('\n');
        if (external.Length > 100000) { throw new InvalidDataException("Too many external init services."); }
        foreach (var line in external)
        {
            token.ThrowIfCancellationRequested();
            var name = line.Trim();
            if (name.Length == 0) { continue; }
            if (name.Length > 16384 || name.Any(char.IsWhiteSpace) || name.Contains('\0') || name.Contains('$'))
            {
                throw new InvalidDataException("External services must be literal names, one per line.");
            }
            services.Add(name);
        }
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var dynamic = new List<string>();
        var invalid = new List<string>();
        var count = 0;
        foreach (var section in sections)
        {
            foreach (var line in section.Body)
            {
                token.ThrowIfCancellationRequested();
                var command = line.Words;
                if (section.Header.Words[0] == "service")
                {
                    if (command[0] != "onrestart") { continue; }
                    command = command[1..];
                }
                if (command.Length == 0 || command[0] is not ("start" or "stop" or "restart" or "enable" or "exec_start"))
                {
                    continue;
                }
                count++;
                var source = text[line.Start..line.End];
                if (command.Skip(1).Any(argument => argument.Contains('$')))
                {
                    dynamic.Add(source);
                    continue;
                }
                // AOSP builtins.cpp: restart alone accepts the extra --only-if-running argument.
                if (command.Length != 2 && !(command[0] == "restart" && command.Length == 3 && command[1] == "--only-if-running")
                    || command[^1].Length == 0)
                {
                    invalid.Add(source);
                    continue;
                }
                if (!services.Contains(command[^1])) { missing.Add(command[^1]); }
            }
        }
        return new InitRcReferenceReport(services.Order(StringComparer.Ordinal).ToArray(), missing.Order(StringComparer.Ordinal).ToArray(),
            dynamic, invalid, ambiguous.Order(StringComparer.Ordinal).ToArray(), count);
    }
}