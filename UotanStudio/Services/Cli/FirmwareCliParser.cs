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
            "verify" => ParseVerify(args),
            "extract" => ParseExtract(args),
            "super-unpack" => ParseSuperUnpack(args),
            "super-pack" => ParseSuperPack(args),
            "erase" => ParseErase(args),
            "reboot" => ParseReboot(args),
            "decompile-dex" => ParseDecompileDex(args),
            "disassemble-dex" => ParseDisassembleDex(args),
            "checksum" => ParseChecksum(args),
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

    private static FirmwareCliParseResult ParseVerify(string[] args)
    {
        string? input = null;
        string? publicKey = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Verify,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--public-key":
                case "-k":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'--public-key' requires a value: --public-key <path>");
                    }
                    publicKey = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'verify': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'verify' takes one vbmeta path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'verify' requires a vbmeta/image path: firmware verify <file> [-k <public-key>]");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Verify,
            InputPath = input,
            PublicKeyPath = publicKey,
        });
    }

    private static FirmwareCliParseResult ParseExtract(string[] args)
    {
        string? input = null;
        string? output = null;
        string? partition = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Extract,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--output-dir":
                case "--output":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'-o' requires a value: -o <dir>");
                    }
                    output = args[++i];
                    break;
                case "--partition":
                case "-p":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'--partition' requires a value: --partition <name>");
                    }
                    partition = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'extract': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'extract' takes one payload path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'extract' requires a payload.bin path: firmware extract <payload.bin> -o <dir> [-p <partition>]");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return FirmwareCliParseResult.Fail("'extract' requires -o <dir>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Extract,
            InputPath = input,
            OutputPath = output,
            Partition = partition,
        });
    }

    private static FirmwareCliParseResult ParseSuperUnpack(string[] args)
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
                    Verb = FirmwareCliVerb.SuperUnpack,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--output-dir":
                case "--output":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'-o' requires a value: -o <dir>");
                    }
                    output = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'super-unpack': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'super-unpack' takes one super.img path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'super-unpack' requires a super.img path: firmware super-unpack <super.img> -o <dir>");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return FirmwareCliParseResult.Fail("'super-unpack' requires -o <dir>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.SuperUnpack,
            InputPath = input,
            OutputPath = output,
        });
    }

    private static FirmwareCliParseResult ParseSuperPack(string[] args)
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
                    Verb = FirmwareCliVerb.SuperPack,
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
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'super-pack': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'super-pack' takes one input directory; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'super-pack' requires an input directory: firmware super-pack <dir> -o <super.img>");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return FirmwareCliParseResult.Fail("'super-pack' requires -o <file>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.SuperPack,
            InputPath = input,
            OutputPath = output,
            FormatName = format,
        });
    }

    private static FirmwareCliParseResult ParseErase(string[] args)
    {
        string? partition = null;
        string? serial = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Erase,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
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
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'erase': {arg}");
                    }
                    if (partition is null)
                    {
                        partition = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'erase' takes one partition name; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(partition))
        {
            return FirmwareCliParseResult.Fail("'erase' requires a partition name: firmware erase <partition> [-d <serial>]");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Erase,
            Partition = partition,
            Serial = serial,
        });
    }

    private static FirmwareCliParseResult ParseReboot(string[] args)
    {
        string? mode = null;
        string? serial = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Reboot,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
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
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'reboot': {arg}");
                    }
                    if (mode is null)
                    {
                        mode = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'reboot' takes one mode argument; got extra: {arg}");
                    }
                    break;
            }
        }

        // Default mode is system (normal reboot).
        mode ??= "system";

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Reboot,
            RebootMode = mode,
            Serial = serial,
        });
    }

    private static FirmwareCliParseResult ParseDecompileDex(string[] args)
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
                    Verb = FirmwareCliVerb.DecompileDex,
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
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'decompile-dex': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'decompile-dex' takes one DEX path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'decompile-dex' requires a DEX file path: firmware decompile-dex <file.dex> [-o <output>]");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.DecompileDex,
            InputPath = input,
            OutputPath = output,
        });
    }

    private static FirmwareCliParseResult ParseDisassembleDex(string[] args)
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
                    Verb = FirmwareCliVerb.DisassembleDex,
                    HelpRequested = true,
                });
            }

            switch (arg)
            {
                case "--output-dir":
                case "--output":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        return FirmwareCliParseResult.Fail("'-o' requires a value: -o <dir>");
                    }
                    output = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return FirmwareCliParseResult.Fail($"Unknown flag for 'disassemble-dex': {arg}");
                    }
                    if (input is null)
                    {
                        input = arg;
                    }
                    else
                    {
                        return FirmwareCliParseResult.Fail($"'disassemble-dex' takes one DEX path; got extra: {arg}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'disassemble-dex' requires a DEX file path: firmware disassemble-dex <file.dex> [-o <dir>]");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.DisassembleDex,
            InputPath = input,
            OutputPath = output,
        });
    }

    private static FirmwareCliParseResult ParseChecksum(string[] args)
    {
        string? input = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsHelp(arg))
            {
                return FirmwareCliParseResult.Ok(new FirmwareCliCommand
                {
                    Verb = FirmwareCliVerb.Checksum,
                    HelpRequested = true,
                });
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                return FirmwareCliParseResult.Fail($"Unknown flag for 'checksum': {arg}");
            }
            if (input is null)
            {
                input = arg;
            }
            else
            {
                return FirmwareCliParseResult.Fail($"'checksum' takes one file path; got extra: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return FirmwareCliParseResult.Fail("'checksum' requires a file path: firmware checksum <file>");
        }

        return FirmwareCliParseResult.Ok(new FirmwareCliCommand
        {
            Verb = FirmwareCliVerb.Checksum,
            InputPath = input,
        });
    }

    private static bool IsHelp(string arg) => arg == "--help" || arg == "-h";

    private static bool IsVersion(string arg) => arg == "--version" || arg == "-v";
}