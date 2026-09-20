using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Cli;

/// <summary>
/// Firmware subcommands backed by the shared format registry, payload extraction, AVB and DEX
/// services. Local-file operations only; device flashing stays behind the device tooling.
/// <para>固件子命令，基于共享格式注册表、payload 提取、AVB 与 DEX 服务实现。
/// 仅覆盖本地文件操作；设备刷写仍由设备工具链承担。</para>
/// </summary>
internal static class FirmwareCommands
{
    /// <summary>
    /// Adds the extended firmware subcommands to the root command.
    /// <para>将扩展的固件子命令加入根命令。</para>
    /// </summary>
    public static void Add(RootCommand root, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var firmware = (Command)root.Children.OfType<Command>().First(c => c.Name == "firmware");
        firmware.Add(CreateInfo());
        firmware.Add(CreatePack());
        firmware.Add(CreateExtract());
        firmware.Add(CreateChecksum());
        firmware.Add(CreateVerify());
        firmware.Add(CreateDisableVerification());
        firmware.Add(CreateDecompileDex());
        firmware.Add(CreateDisassembleDex());

        Command CreateInfo()
        {
            var command = new Command("info", "Read format metadata for one firmware file.");
            var input = new Argument<FileInfo>("input");
            var json = new Option<bool>("--json") { Description = "Emit machine-readable JSON." };
            command.Add(input);
            command.Add(json);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                if (!RequireInput(file, error)) return 3;
                try
                {
                    FirmwareFormatRegistry.RegisterAll();
                    var handler = FormatDetector.Detect(file!.FullName);
                    if (handler is null)
                    {
                        await error.WriteLineAsync("Unknown firmware format.").ConfigureAwait(false);
                        return 3;
                    }

                    var result = await handler.ReadMetadataAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                    if (parseResult.GetValue(json))
                    {
                        var payload = new FirmwareInfoJson(handler.FormatName, result.Success, result.Summary,
                            result.Entries, new Dictionary<string, string>(result.Metadata));
                        await output.WriteLineAsync(JsonSerializer.Serialize(payload, CliJsonContext.Default.FirmwareInfoJson)).ConfigureAwait(false);
                    }
                    else
                    {
                        await output.WriteLineAsync(handler.FormatName).ConfigureAwait(false);
                        await output.WriteLineAsync(result.Summary).ConfigureAwait(false);
                        foreach (var entry in result.Entries)
                        {
                            await output.WriteLineAsync(entry).ConfigureAwait(false);
                        }
                    }

                    return result.Success ? 0 : 3;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreatePack()
        {
            var command = new Command("pack", "Pack an input directory/file back into the detected image format.");
            var input = new Argument<FileInfo>("input");
            var outputOption = new Option<FileInfo>("--output") { Description = "Target image file." };
            command.Add(input);
            command.Add(outputOption);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                var target = parseResult.GetValue(outputOption);
                if (!RequireInput(file, error) || target is null)
                {
                    await error.WriteLineAsync("Both input and --output are required.").ConfigureAwait(false);
                    return 3;
                }

                try
                {
                    FirmwareFormatRegistry.RegisterAll();
                    if (FormatDetector.Detect(file!.FullName) is not IWritableFormatHandler handler)
                    {
                        await error.WriteLineAsync("The detected format does not support packing.").ConfigureAwait(false);
                        return 3;
                    }

                    target.Directory?.Create();
                    var result = await handler.PackAsync(file.FullName, target.FullName, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(result.Summary).ConfigureAwait(false);
                    return result.Success ? 0 : 3;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreateExtract()
        {
            var command = new Command("extract", "Extract partitions from an Android OTA payload.bin.");
            var input = new Argument<FileInfo>("input");
            var outputOption = new Option<DirectoryInfo>("--output") { Description = "Destination directory." };
            var partitions = new Option<string[]?>("--partitions") { Description = "Partition names to extract; defaults to all.", AllowMultipleArgumentsPerToken = true };
            command.Add(input);
            command.Add(outputOption);
            command.Add(partitions);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                var directory = parseResult.GetValue(outputOption);
                if (!RequireInput(file, error) || directory is null)
                {
                    await error.WriteLineAsync("Both input and --output are required.").ConfigureAwait(false);
                    return 3;
                }

                try
                {
                    Directory.CreateDirectory(directory.FullName);
                    await PayloadParser.ExtractAsync(file!.FullName,
                        parseResult.GetValue(partitions),
                        directory.FullName,
                        cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Payload extracted to {directory.FullName}").ConfigureAwait(false);
                    return 0;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreateChecksum()
        {
            var command = new Command("checksum", "Compute the streaming SHA-256 checksum of a file.");
            var input = new Argument<FileInfo>("input");
            command.Add(input);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                if (!RequireInput(file, error)) return 3;
                try
                {
                    await using var stream = file!.OpenRead();
                    var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(Convert.ToHexString(hash).ToLowerInvariant()).ConfigureAwait(false);
                    return 0;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreateVerify()
        {
            var command = new Command("verify", "Verify the AVB signature of a vbmeta/boot image.");
            var input = new Argument<FileInfo>("input");
            var json = new Option<bool>("--json") { Description = "Emit machine-readable JSON." };
            command.Add(input);
            command.Add(json);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                if (!RequireInput(file, error)) return 3;
                try
                {
                    await using var stream = file!.OpenRead();
                    var result = await Task.Run(() => AvbVerifier.VerifyFromStream(stream, stream.Length), cancellationToken).ConfigureAwait(false);
                    if (parseResult.GetValue(json))
                    {
                        var payload = new AvbVerifyJson(result.IsSigned, result.IsValid, result.ErrorMessage);
                        await output.WriteLineAsync(JsonSerializer.Serialize(payload, CliJsonContext.Default.AvbVerifyJson)).ConfigureAwait(false);
                    }
                    else
                    {
                        await output.WriteLineAsync($"Signed: {result.IsSigned}").ConfigureAwait(false);
                        await output.WriteLineAsync($"Valid: {result.IsValid}").ConfigureAwait(false);
                        if (result.ErrorMessage is not null)
                        {
                            await output.WriteLineAsync(result.ErrorMessage).ConfigureAwait(false);
                        }
                    }

                    return result.IsSigned && result.IsValid ? 0 : 3;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreateDisableVerification()
        {
            var command = new Command("disable-verification", "Disable AVB verification flags on a vbmeta image.");
            var input = new Argument<FileInfo>("input");
            var outputOption = new Option<FileInfo>("--output") { Description = "Target vbmeta file; must differ from the input." };
            command.Add(input);
            command.Add(outputOption);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                var target = parseResult.GetValue(outputOption);
                if (!RequireInput(file, error) || target is null)
                {
                    await error.WriteLineAsync("Both input and --output are required.").ConfigureAwait(false);
                    return 3;
                }

                try
                {
                    target.Directory?.Create();
                    var result = await AvbSigner.DisableVbmetaVerificationAsync(file!.FullName, target.FullName, cancellationToken).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        await error.WriteLineAsync(result.ErrorMessage ?? "Failed to disable AVB verification.").ConfigureAwait(false);
                        return 3;
                    }

                    await output.WriteLineAsync($"Verification disabled: {result.OutputPath} ({result.OutputSize} bytes)").ConfigureAwait(false);
                    return 0;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreateDecompileDex()
        {
            var command = new Command("decompile-dex", "Decompile a DEX file to pseudo-Java source.");
            var input = new Argument<FileInfo>("input");
            var outputOption = new Option<FileInfo>("--output") { Description = "Target source file." };
            command.Add(input);
            command.Add(outputOption);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                var target = parseResult.GetValue(outputOption);
                if (!RequireInput(file, error) || target is null)
                {
                    await error.WriteLineAsync("Both input and --output are required.").ConfigureAwait(false);
                    return 3;
                }

                try
                {
                    var dex = await Task.Run(() => DexFile.Load(file!.FullName), cancellationToken).ConfigureAwait(false);
                    var source = await Task.Run(() => LightweightDecompiler.DecompileAll(dex), cancellationToken).ConfigureAwait(false);
                    target.Directory?.Create();
                    await File.WriteAllTextAsync(target.FullName, source, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Decompiled to {target.FullName}").ConfigureAwait(false);
                    return 0;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }

        Command CreateDisassembleDex()
        {
            var command = new Command("disassemble-dex", "Disassemble a DEX file to Smali text.");
            var input = new Argument<FileInfo>("input");
            var outputOption = new Option<FileInfo>("--output") { Description = "Target Smali file." };
            command.Add(input);
            command.Add(outputOption);
            command.SetAction(async parseResult =>
            {
                var file = parseResult.GetValue(input);
                var target = parseResult.GetValue(outputOption);
                if (!RequireInput(file, error) || target is null)
                {
                    await error.WriteLineAsync("Both input and --output are required.").ConfigureAwait(false);
                    return 3;
                }

                try
                {
                    var dex = await Task.Run(() => DexFile.Load(file!.FullName), cancellationToken).ConfigureAwait(false);
                    var smali = await Task.Run(() => Disassembler.DisassembleAll(dex), cancellationToken).ConfigureAwait(false);
                    target.Directory?.Create();
                    await File.WriteAllTextAsync(target.FullName, smali, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"Disassembled to {target.FullName}").ConfigureAwait(false);
                    return 0;
                }
                catch (OperationCanceledException) { return 7; }
                catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
            });
            return command;
        }
    }

    private static bool RequireInput(FileInfo? file, TextWriter error)
    {
        if (file is null || !file.Exists)
        {
            error.WriteLine("Input file does not exist.");
            return false;
        }

        return true;
    }
}

internal sealed record FirmwareInfoJson(
    string Format,
    bool Success,
    string Summary,
    IReadOnlyList<string> Entries,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record AvbVerifyJson(bool IsSigned, bool IsValid, string? ErrorMessage);