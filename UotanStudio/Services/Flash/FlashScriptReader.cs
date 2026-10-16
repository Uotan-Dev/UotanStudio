namespace UotanStudio.Services.Flash;

/// <summary>Imports a small, explicit subset of legacy Fastboot scripts without invoking a shell.<para>导入旧式 Fastboot 脚本中有界的显式子集，不调用 shell。</para></summary>
public static class FlashScriptReader
{
    /// <summary>Parses ordered commands, rejecting any unsupported statement before execution.<para>解析按序排列的命令，在执行前拒绝任何不支持的语句。</para></summary>
    /// <param name="path">Script file to import.<para>要导入的脚本文件。</para></param>
    /// <returns>The ordered plan built from supported commands.<para>由受支持命令构成的有序计划。</para></returns>
    public static FlashManifest Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024 * 1024)
        {
            throw new InvalidDataException("Flash script is missing or too large.");
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var steps = new List<FlashStep>();
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            if (lineNumber > 4096)
            {
                throw new InvalidDataException("Flash script has too many lines.");
            }

            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("::", StringComparison.Ordinal) ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("@echo off", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith('@')) line = line[1..];
            var tokens = Tokenize(line, lineNumber);
            if (tokens.Length < 2 || !tokens[0].Equals("fastboot", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported flash script statement on line {lineNumber}.");
            }

            var index = 1;
            if (tokens[index] is "%*" or "$*") index++;
            if (index >= tokens.Length)
            {
                throw new InvalidDataException($"Missing flash command on line {lineNumber}.");
            }

            var command = tokens[index++].ToLowerInvariant();
            if (command == "wipe-super" && index + 1 == tokens.Length)
            {
                steps.Add(ReadSuperEmpty(tokens[index], root, lineNumber));
                continue;
            }
            if (command == "reboot" && index == tokens.Length)
            {
                steps.Add(new FlashStep("reboot", ""));
                continue;
            }

            if (command == "set_active" && index + 1 == tokens.Length &&
                tokens[index] is "a" or "b" or "other")
            {
                steps.Add(new FlashStep(command, tokens[index]));
                continue;
            }

            if (index >= tokens.Length || !FlashManifestReader.IsPartitionName(tokens[index]))
            {
                throw new InvalidDataException($"Invalid partition on line {lineNumber}.");
            }

            var partition = tokens[index++];
            if (partition.Equals("super_empty", StringComparison.OrdinalIgnoreCase) && command != "flash")
                throw new InvalidDataException($"Invalid super_empty operation on line {lineNumber}.");
            if (command == "erase" && index == tokens.Length)
            {
                steps.Add(new FlashStep(command, partition));
                continue;
            }

            if (command != "flash" || index + 1 != tokens.Length)
            {
                throw new InvalidDataException($"Unsupported flash command on line {lineNumber}.");
            }

            var relative = tokens[index];
            if (relative.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase)) relative = relative[5..];
            if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Split(['/', '\\']).Any(segment => segment is "." or ".." or "") ||
                !relative.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                !relative.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported image path on line {lineNumber}.");
            }

            var imagePath = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
            if (!imagePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Image path escapes script directory on line {lineNumber}.");
            }

            var image = new FileInfo(imagePath);
            if (!image.Exists || image.Length == 0)
            {
                throw new InvalidDataException($"Missing or empty image on line {lineNumber}.");
            }

            var isSuperEmpty = partition.Equals("super_empty", StringComparison.OrdinalIgnoreCase);
            if (isSuperEmpty)
            {
                if (!Path.GetFileName(imagePath).Equals("super_empty.img", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Invalid super_empty image on line {lineNumber}.");
                _ = FlashSuperEmptyService.Prepare(imagePath);
            }
            steps.Add(new FlashStep(isSuperEmpty ? "wipe_super" : command, partition, imagePath, image.Length));
        }

        if (steps.Count == 0)
        {
            throw new InvalidDataException("Flash script contains no supported operations.");
        }
        if (steps.SkipLast(1).Any(step => step.Command == "reboot"))
        {
            throw new InvalidDataException("Reboot must be the final flash script step.");
        }

        return new FlashManifest(null, steps);
    }

    private static FlashStep ReadSuperEmpty(string relative, string root, int lineNumber)
    {
        if (relative.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase)) relative = relative[5..];
        if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split(['/', '\\'])
                .Any(segment => segment is "." or ".." or "") ||
            !Path.GetFileName(relative).Equals("super_empty.img", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid super_empty path on line {lineNumber}.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"super_empty path escapes script directory on line {lineNumber}.");
        _ = FlashSuperEmptyService.Prepare(path);
        return new FlashStep("wipe_super", "super_empty", path, new FileInfo(path).Length);
    }

    private static string[] Tokenize(string line, int lineNumber)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var character in line)
        {
            if (character is '"' or '\'')
            {
                if (quote == '\0') quote = character;
                else if (quote == character) quote = '\0';
                else current.Append(character);
                continue;
            }

            if (character is '&' or '|' or ';' or '>' or '<' or '`' or '\r' or '\n')
                throw new InvalidDataException($"Unsupported shell operator on line {lineNumber}.");
            if (char.IsWhiteSpace(character) && quote == '\0')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else current.Append(character);
        }
        if (quote != '\0') throw new InvalidDataException($"Unterminated quote on line {lineNumber}.");
        if (current.Length > 0) tokens.Add(current.ToString());
        return [.. tokens];
    }
}