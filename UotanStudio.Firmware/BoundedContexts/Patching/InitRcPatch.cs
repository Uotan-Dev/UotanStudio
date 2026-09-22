using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Android init section selectors. Imports are standalone sections.</summary>
public enum InitRcSection { Service, Action, Import }

/// <summary>Lexical statement edits within explicitly selected init sections.</summary>
public enum InitRcOperation { Replace, Delete }

/// <summary>Placement relative to a uniquely matched complete body statement.</summary>
public enum InitRcInsertion { End, BeforeAnchor, AfterAnchor }

/// <summary>Bounded Android init lexer and source-preserving statement editor.</summary>
public static partial class InitRcPatch
{
    private sealed record Statement(int Start, int End, int LineStart, int LineEnd, string[] Words, (int Start, int End)[] Ranges);
    private sealed record Section(Statement Header, List<Statement> Body);

    /// <summary>Replaces an existing decoded argument, retaining every unselected source span. Index zero follows the directive.</summary>
    public static TextPatchResult EditArgument(string text, InitRcSection kind, string selector, string directive,
        int argumentIndex, string value, bool checkOldValue, string expectedOldValue, int expectedSections,
        int expectedMatches, CancellationToken token)
    {
        if (argumentIndex is < 0 or > 1022 || value.Length > 16384 || value.Contains('\0'))
        {
            throw new InvalidDataException("Invalid init argument index or value.");
        }
        var matches = Select(text, kind, selector, directive, expectedSections, expectedMatches, token);
        if (directive.Length == 0 && matches.Count != 1)
        {
            throw new InvalidDataException("Header argument editing requires exactly one selected section.");
        }
        var wordIndex = argumentIndex + 1;
        var encoded = value.Length == 0 ? "\"\"" : string.Concat(value.Select(character => character switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            ' ' => "\\ ",
            '\\' => "\\\\",
            '"' => "\\\"",
            '#' => "\\#",
            _ => character.ToString()
        }));
        var output = new StringBuilder();
        var position = 0;
        foreach (var line in matches)
        {
            token.ThrowIfCancellationRequested();
            if (wordIndex >= line.Words.Length)
            {
                throw new InvalidDataException("Selected init statement does not contain the requested argument.");
            }
            if (checkOldValue && line.Words[wordIndex] != expectedOldValue)
            {
                throw new InvalidDataException("Init argument does not match the expected decoded old value.");
            }
            var range = line.Ranges[wordIndex];
            var replacement = line.Words[wordIndex] == value ? text[range.Start..range.End] : encoded;
            if (directive.Length == 0)
            {
                return ReplaceHeader(text, kind, selector, text[line.Start..range.Start] + replacement + text[range.End..line.End],
                    false, "", token);
            }
            if ((long)output.Length + range.Start - position + replacement.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Init output exceeds the configuration limit.");
            }
            output.Append(text, position, range.Start - position).Append(replacement);
            position = range.End;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        Parse(result, token);
        return new TextPatchResult(result, matches.Count, result != text);
    }

    /// <summary>Replaces one section header without changing its kind or rewriting references to its identity.</summary>
    public static TextPatchResult ReplaceHeader(string text, InitRcSection kind, string selector, string replacement,
        bool checkOldValue, string expectedOldValue, CancellationToken token)
    {
        var selected = Select(text, kind, selector, "", 1, 1, token)[0];
        var supplied = Parse(replacement, token);
        if (supplied.Count != 1 || supplied[0].Body.Count != 0 || supplied[0].Header.Start != 0
            || supplied[0].Header.End != replacement.Length || supplied[0].Header.Words[0] != selected.Words[0])
        {
            throw new InvalidDataException("Supply exactly one header of the selected section kind, without body or comments.");
        }
        if (checkOldValue && text[selected.Start..selected.End] != expectedOldValue)
        {
            throw new InvalidDataException("Init header does not match the expected old source text.");
        }
        var desired = supplied[0].Header.Words;
        if (Parse(text, token).Any(section => section.Header.Start != selected.Start
            && section.Header.Words[0] == desired[0]
            && (kind == InitRcSection.Action ? section.Header.Words.Skip(1).SequenceEqual(desired.Skip(1))
                : section.Header.Words[1] == desired[1])))
        {
            throw new InvalidDataException("The new init header identity conflicts with another section.");
        }
        if ((long)text.Length - (selected.End - selected.Start) + replacement.Length > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        var result = text[..selected.Start] + replacement + text[selected.End..];
        Parse(result, token);
        return new TextPatchResult(result, 1, result != text);
    }

    /// <summary>Appends one complete section, preserving an equivalent existing section and rejecting identity conflicts.</summary>
    public static TextPatchResult AddSection(string text, string sectionText, CancellationToken token)
    {
        var supplied = Parse(sectionText, token);
        if (supplied.Count != 1)
        {
            throw new InvalidDataException("Supply exactly one service, action or import section.");
        }
        var desired = supplied[0];
        var header = desired.Header.Words;
        var existing = Parse(text, token).Where(section => section.Header.Words[0] == header[0]
            && (header[0] == "on" ? section.Header.Words.Skip(1).SequenceEqual(header.Skip(1))
                : section.Header.Words[1] == header[1])).ToArray();
        if (existing.Length > 1)
        {
            throw new InvalidDataException("Multiple init sections share the requested identity.");
        }
        if (existing.Length == 1)
        {
            var current = existing[0];
            if (!current.Header.Words.SequenceEqual(header) || current.Body.Count != desired.Body.Count
                || current.Body.Where((line, index) => !line.Words.SequenceEqual(desired.Body[index].Words)).Any())
            {
                throw new InvalidDataException("An existing init section has different header arguments or ordered body statements.");
            }
            return new TextPatchResult(text, 1, false);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        var ending = sectionText.EndsWith('\n') ? "" : newline;
        if ((long)text.Length + separator.Length + sectionText.Length + ending.Length > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        var result = text + separator + sectionText + ending;
        Parse(result, token);
        return new TextPatchResult(result, 0, true);
    }

    /// <summary>Appends a statement if its explicit directive/argument identity is absent; conflicting identities fail.</summary>
    public static TextPatchResult Add(string text, InitRcSection kind, string selector, string statement,
        int identityArguments, int expectedSections, CancellationToken token,
        InitRcInsertion placement = InitRcInsertion.End, string anchor = "")
    {
        if (kind == InitRcSection.Import || expectedSections < 1)
        {
            throw new InvalidDataException("Adding a body statement requires at least one service or action section.");
        }
        var supplied = Lex(statement, token);
        if (supplied.Count != 1 || supplied[0].Start != 0 || supplied[0].End != statement.Length
            || IsHeader(supplied[0].Words[0]) || identityArguments < 0 || identityArguments >= supplied[0].Words.Length)
        {
            throw new InvalidDataException("Supply one complete body statement and a valid number of identity arguments.");
        }
        var desired = supplied[0].Words;
        if (!Enum.IsDefined(placement)) { throw new InvalidDataException("Unsupported init insertion placement."); }
        string[] anchorWords = [];
        if (placement != InitRcInsertion.End)
        {
            var anchors = Lex(anchor, token);
            if (anchors.Count != 1 || IsHeader(anchors[0].Words[0])
                || anchors[0].Start != 0 || anchors[0].End != anchor.Length)
            {
                throw new InvalidDataException("Anchor must be one complete body statement without trailing comments.");
            }
            anchorWords = anchors[0].Words;
        }
        var headers = Select(text, kind, selector, "", expectedSections, expectedSections, token)
            .Select(header => header.Start).ToHashSet();
        var sections = Parse(text, token).Where(section => headers.Contains(section.Header.Start));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new StringBuilder();
        var position = 0;
        var existing = 0;
        foreach (var section in sections)
        {
            token.ThrowIfCancellationRequested();
            Statement? anchorLine = null;
            if (placement != InitRcInsertion.End)
            {
                var anchors = section.Body.Where(line => line.Words.SequenceEqual(anchorWords)).ToArray();
                if (anchors.Length != 1)
                {
                    throw new InvalidDataException("Each selected section must contain exactly one matching anchor statement.");
                }
                anchorLine = anchors[0];
            }
            var matches = section.Body.Where(line => line.Words.Length > identityArguments
                && line.Words.Take(identityArguments + 1).SequenceEqual(desired.Take(identityArguments + 1))).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !matches[0].Words.SequenceEqual(desired))
            {
                throw new InvalidDataException("Existing init statement identity is duplicated or has different arguments.");
            }
            if (matches.Length == 1)
            {
                if (anchorLine is not null)
                {
                    var expectedIndex = section.Body.IndexOf(anchorLine) + (placement == InitRcInsertion.BeforeAnchor ? -1 : 1);
                    if (section.Body.IndexOf(matches[0]) != expectedIndex)
                    {
                        throw new InvalidDataException("The existing init statement is not adjacent to the requested side of its anchor.");
                    }
                }
                existing++;
                continue;
            }
            var last = section.Body.LastOrDefault() ?? section.Header;
            var insertion = anchorLine is null ? last.LineEnd
                : placement == InitRcInsertion.BeforeAnchor ? anchorLine.LineStart : anchorLine.LineEnd;
            var separator = insertion > 0 && text[insertion - 1] != '\n' ? newline : "";
            var addition = separator + "    " + statement + newline;
            if ((long)output.Length + insertion - position + addition.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Init output exceeds the configuration limit.");
            }
            output.Append(text, position, insertion - position).Append(addition);
            position = insertion;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        Parse(result, token);
        return new TextPatchResult(result, existing, result != text);
    }

    /// <summary>Reads raw headers (empty directive) or matching body statements, without expanding imports or properties.</summary>
    public static IReadOnlyList<string> Read(string text, InitRcSection kind, string selector, string directive,
        int expectedSections, int expectedMatches, CancellationToken token)
        => Select(text, kind, selector, directive, expectedSections, expectedMatches, token)
            .Select(line => text[line.Start..line.End]).ToArray();

    /// <summary>Replaces or deletes matching body statements; headers are read-only in this API.</summary>
    public static TextPatchResult Apply(string text, InitRcSection kind, string selector, string directive,
        InitRcOperation operation, string replacement, bool checkOldValue, string expectedOldValue,
        int expectedSections, int expectedMatches, CancellationToken token)
    {
        if (!Enum.IsDefined(operation) || string.IsNullOrWhiteSpace(directive) || kind == InitRcSection.Import)
        {
            throw new InvalidDataException("Editing requires a service/action body directive and a supported operation.");
        }
        if (operation == InitRcOperation.Replace)
        {
            var lines = Lex(replacement, token);
            if (lines.Count != 1 || lines[0].Start != 0 || lines[0].End != replacement.Length
                || IsHeader(lines[0].Words[0]) || lines[0].Words[0] != directive)
            {
                throw new InvalidDataException("Replacement must be exactly one statement with the selected directive name.");
            }
        }
        var matches = Select(text, kind, selector, directive, expectedSections, expectedMatches, token);
        var output = new StringBuilder();
        var position = 0;
        foreach (var line in matches)
        {
            token.ThrowIfCancellationRequested();
            if (checkOldValue && text[line.Start..line.End] != expectedOldValue)
            {
                throw new InvalidDataException("Init statement does not match the expected old source text.");
            }
            var start = operation == InitRcOperation.Delete ? line.LineStart : line.Start;
            var end = operation == InitRcOperation.Delete ? line.LineEnd : line.End;
            var value = operation == InitRcOperation.Delete ? "" : replacement;
            if ((long)output.Length + start - position + value.Length > TextPatch.MaxCharacters)
            {
                throw new InvalidDataException("Init output exceeds the configuration limit.");
            }
            output.Append(text, position, start - position).Append(value);
            position = end;
        }
        if ((long)output.Length + text.Length - position > TextPatch.MaxCharacters)
        {
            throw new InvalidDataException("Init output exceeds the configuration limit.");
        }
        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        Parse(result, token);
        return new TextPatchResult(result, matches.Count, result != text);
    }

    private static List<Statement> Select(string text, InitRcSection kind, string selector, string directive,
        int expectedSections, int expectedMatches, CancellationToken token)
    {
        if (!Enum.IsDefined(kind) || expectedSections is < 0 or > 100000 || expectedMatches is < 0 or > 100000)
        {
            throw new InvalidDataException("Invalid init selector or expected count.");
        }
        var selection = Lex(selector, token);
        if (selection.Count != 1 || selection[0].Words.Length == 0
            || kind != InitRcSection.Action && selection[0].Words.Length != 1)
        {
            throw new InvalidDataException("Selector must contain one service name, one import path, or one action trigger expression.");
        }
        if (directive.Length > 16384 || directive.Any(char.IsWhiteSpace) || directive.Contains('\0'))
        {
            throw new InvalidDataException("Directive must be a single literal name or empty for a header query.");
        }
        var keyword = kind switch { InitRcSection.Service => "service", InitRcSection.Action => "on", _ => "import" };
        var sections = Parse(text, token).Where(section => section.Header.Words[0] == keyword
            && (kind == InitRcSection.Action
                ? section.Header.Words.Skip(1).SequenceEqual(selection[0].Words)
                : section.Header.Words[1] == selection[0].Words[0])).ToArray();
        if (sections.Length != expectedSections)
        {
            throw new InvalidDataException($"Expected {expectedSections} init sections, found {sections.Length}.");
        }
        var matches = sections.SelectMany(section => directive.Length == 0
            ? new[] { section.Header } : section.Body.Where(line => line.Words[0] == directive)).ToList();
        if (matches.Count != expectedMatches)
        {
            throw new InvalidDataException($"Expected {expectedMatches} init statements, found {matches.Count}.");
        }
        return matches;
    }

    private static bool IsHeader(string word) => word is "service" or "on" or "import";

    private static List<Section> Parse(string text, CancellationToken token)
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (var line in Lex(text, token))
        {
            token.ThrowIfCancellationRequested();
            if (IsHeader(line.Words[0]))
            {
                if (line.Words.Length < (line.Words[0] == "service" ? 3 : 2)
                    || line.Words[0] == "import" && line.Words.Length != 2)
                {
                    throw new InvalidDataException("Malformed init section header.");
                }
                current = new Section(line, []);
                sections.Add(current);
            }
            else if (current is null || current.Header.Words[0] == "import")
            {
                throw new InvalidDataException("Init statement has no service or action section.");
            }
            else
            {
                current.Body.Add(line);
            }
        }
        return sections;
    }

    // Follows system/core/init/tokenizer.cpp: # starts a comment only between tokens;
    // quoted text is literal, and escapes/continuations are interpreted outside quotes.
    private static List<Statement> Lex(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TextPatch.ValidateSize(text);
        if (text.Contains('\0')) { throw new InvalidDataException("NUL is not permitted in init text."); }
        var result = new List<Statement>();
        var words = new List<string>();
        var ranges = new List<(int Start, int End)>();
        var position = 0;
        var lineStart = 0;
        var start = 0;
        var end = 0;
        var physicalLines = 1;
        while (position < text.Length)
        {
            token.ThrowIfCancellationRequested();
            var character = text[position];
            if (character is ' ' or '\t' or '\r') { position++; continue; }
            if (character == '#')
            {
                while (position < text.Length && text[position] != '\n') { position++; }
                continue;
            }
            if (character == '\n')
            {
                position++;
                CountLine();
                Finish();
                lineStart = position;
                continue;
            }
            if (words.Count == 0) { start = position; }
            var tokenStart = position;
            var value = new StringBuilder();
            while (position < text.Length && text[position] is not (' ' or '\t' or '\r' or '\n'))
            {
                token.ThrowIfCancellationRequested();
                character = text[position++];
                if (character == '"')
                {
                    while (position < text.Length && text[position] != '"')
                    {
                        if (text[position] == '\n') { CountLine(); }
                        value.Append(text[position++]);
                        CheckToken();
                    }
                    if (position == text.Length) { throw new InvalidDataException("Unterminated init quote."); }
                    position++;
                }
                else if (character == '\\')
                {
                    if (position == text.Length) { throw new InvalidDataException("Dangling init escape."); }
                    character = text[position++];
                    if (character == '\r')
                    {
                        if (position == text.Length || text[position] != '\n') { continue; }
                        character = text[position++];
                    }
                    if (character == '\n')
                    {
                        CountLine();
                        while (position < text.Length && text[position] is ' ' or '\t') { position++; }
                    }
                    else
                    {
                        value.Append(character switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => character });
                    }
                }
                else
                {
                    value.Append(character);
                }
                CheckToken();
            }
            end = position;
            words.Add(value.ToString());
            ranges.Add((tokenStart, position));
            if (words.Count > 1024) { throw new InvalidDataException("Too many init statement arguments."); }

            void CheckToken()
            {
                if (value.Length > 16384) { throw new InvalidDataException("Init token exceeds the limit."); }
            }
        }
        Finish();
        return result;

        void CountLine()
        {
            if (++physicalLines > 100000) { throw new InvalidDataException("Too many init lines."); }
        }

        void Finish()
        {
            if (words.Count == 0) { return; }
            result.Add(new Statement(start, end, lineStart, position, [.. words], [.. ranges]));
            words.Clear();
            ranges.Clear();
        }
    }
}