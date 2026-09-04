namespace UotanStudio.Services.Cli;

/// <summary>
/// Hand-rolled parser for the <c>firmware</c> CLI subcommand. AOT-friendly (no reflection).
/// Accepts the verb and its arguments; returns a <see cref="FirmwareCliParseResult"/>.
/// <para>手写解析器（AOT 友好，无反射）。接受子命令与其参数，返回 <see cref="FirmwareCliParseResult"/>。</para>
/// </summary>
public static class FirmwareCliParser
{
    /// <summary>
    /// Parses firmware CLI arguments (the tokens after the literal <c>firmware</c> token).
    /// </summary>
    /// <para>解析固件 CLI 参数（字面量 <c>firmware</c> 之后的 token）。</para>
    public static FirmwareCliParseResult Parse(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            // No verb: treat as help.
            return FirmwareCliParseResult.Ok(new FirmwareCliCommand
            {
                Verb = FirmwareCliVerb.List,
                HelpRequested = true,
            });
        }

        // Global help/version flags may appear as the first token instead of a verb.
        var first = args[0];
        if (IsHelp(first))
        {
            return FirmwareCliParseResult.Ok(new FirmwareCliCommand
            {
                Verb = FirmwareCliVerb.List,
                HelpRequested = true,
            });
        }

        if (IsVersion(first))
        {
            return FirmwareCliParseResult.Ok(new FirmwareCliCommand
            {
                Verb = FirmwareCliVerb.List,
                VersionRequested = true,
            });
        }

        return first.ToLowerInvariant() switch
        {
            "list" => ParseList(args),
            "info" => ParseInfo(args),
            "unpack" => ParseUnpack(args),
            "pack" => ParsePack(args),
            "flash" => ParseFlash(args),
            "disable-verification" => ParseDisableVerification(args),
            _ => FirmwareCliParseResult.Fail($"Unknown firmware subcommand: {first}"),
        };
    }

    private static FirmwareCliParseResult ParseList(string[] args)
    {
        // `list` takes no arguments; ignore extras but reject unknown flags.
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("-", StringComparison.Ordinal))
            {
                return FirmwareCliParseResult.Fail($"Unknown flag for 'list': {args[i]}");
            }
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.List,
        });
    }

    private static FirmwareCliParseResult ParseInfo(string[] args)
    {
        string? input = null;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Info,
                    HelpRequested = true,
                });
            }

            if (arg == "--format" || arg == "-f")
            {
                return FirmwareCliParseResult.Fail("'info' does not accept --format; detection is automatic.");
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                return FirmwareCliParseResult.Fail($"Unknown flag for 'info': {arg}");
            }

            if (input is null)
            {
                input = arg;
            }
            else
            {
                return FirmwareCliParseResult.Fail($"'info' takes exactly one file path; got extra: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'info' requires a file path: firmware info <file>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Info,
            InputPath = input,
        });
    }

    private static FirmwareCliParseResult ParseUnpack(string[] args)
    {
        string? input = null;
        string? output = null;
        string? format = null;
        var noOverwrite = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Unpack,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--output-dir":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'-o' requires a value: -o <dir>");
                    }
                    output = args[++i];
                    break;
                case "--format":
                case "-f":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'--format' requires a value: --format <name>");
                    }
                    format = args[++i];
                    break;
                case "--no-overwrite":
                    noOverwrite = true;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'unpack': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'unpack' takes one file path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'unpack' requires a file path: firmware unpack <file> -o <dir>");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return FirmwareCliParseResult.Fail("'unpack' requires -o <dir>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Unpack,
            InputPath = input,
            OutputPath = output,
            FormatName = format,
            NoOverwrite = noOverwrite,
        });
    }

    private static FirmwareCliParseResult ParsePack(string[] args)
    {
        string? input = null;
        string? output = null;
        string? format = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Pack,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--output":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'-o' requires a value: -o <file>");
                    }
                    output = args[++i];
                    break;
                case "--format":
                case "-f":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'--format' requires a value: --format <name>");
                    }
                    format = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'pack': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'pack' takes one input path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'pack' requires an input path: firmware pack <input> -o <file>");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return FirmwareCliParseResult.Fail("'pack' requires -o <file>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Pack,
            InputPath = input,
            OutputPath = output,
            FormatName = format,
        });
    }

    private static FirmwareCliParseResult ParseFlash(string[] args)
    {
        string? input = null;
        string? partition = null;
        string? serial = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Flash,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--partition":
                case "-p":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'--partition' requires a value: --partition <name>");
                    }
                    partition = args[++i];
                    break;
                case "--serial":
                case "-d":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'--serial' requires a value: --serial <serial>");
                    }
                    serial = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'flash': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'flash' takes one image path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'flash' requires an image path: firmware flash <image> -p <partition>");
        }

        if (string.IsNullOrWhiteSpace(partition))
        {
            return FirmwareCliParseResult.Fail("'flash' requires --partition: firmware flash <image> -p <partition>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Flash,
            InputPath = input,
            Partition = partition,
            Serial = serial,
        });
    }

    private static FirmwareCliParseResult ParseDisableVerification(string[] args)
    {
        string? input = null;
        string? output = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.DisableVerification,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--output":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'-o' requires a value: -o <file>");
                    }
                    output = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'disable-verification': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'disable-verification' takes one vbmeta path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'disable-verification' requires a vbmeta path: firmware disable-verification <vbmeta> -o <file>");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return FirmwareCliParseResult.Fail("'disable-verification' requires -o <file>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.DisableVerification,
            InputPath = input,
            OutputPath = output,
        });
    }

    private static bool IsHelp(string arg) => arg == "--help" || arg == "-h";

    private static bool IsVersion(string arg) => arg == "--version" || arg == "-v";
}