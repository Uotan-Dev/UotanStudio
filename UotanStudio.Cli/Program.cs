using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json.Serialization;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;

namespace UotanStudio.Cli;

/// <summary>
/// Command-line entry point shared by the standalone host and the desktop executable.
/// <para>独立宿主与桌面可执行文件共用的命令行入口。</para>
/// </summary>
public static class CliProgram
{
    /// <summary>
    /// Runs the command tree and returns a process exit code.
    /// <para>运行命令树并返回进程退出码。</para>
    /// </summary>
    /// <param name="args">Command-line arguments. <para>命令行参数。</para></param>
    /// <param name="output">Standard output writer; defaults to the console. <para>标准输出写入器；默认控制台。</para></param>
    /// <param name="error">Standard error writer; defaults to the console. <para>标准错误写入器；默认控制台。</para></param>
    /// <param name="cancellationToken">Cancellation token propagated to asynchronous commands. <para>传播到异步命令的取消令牌。</para></param>
    /// <returns>The process exit code. <para>进程退出码。</para></returns>
    public static Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;
        var root = CreateRootCommand(outWriter, errWriter, cancellationToken);
        // Help text and parse errors are written by System.CommandLine through the invocation
        // configuration, not the command delegates. Without this the injected TextWriters see an
        // empty stream and tests (or callers redirecting output) silently lose --help output.
        // 帮助文本与解析错误由 System.CommandLine 通过调用配置写出，而非命令委托。若不显式
        // 传入，注入的 TextWriter 会收到空流，测试或重定向输出的调用方会丢失 --help 内容。
        var configuration = new InvocationConfiguration
        {
            Output = outWriter,
            Error = errWriter,
        };
        return root.Parse(args).InvokeAsync(configuration, cancellationToken);
    }

    /// <summary>
    /// Returns true when arguments explicitly request command-line mode.
    /// <para>判断参数是否显式请求命令行模式。</para>
    /// </summary>
    /// <param name="args">Command-line arguments. <para>命令行参数。</para></param>
    /// <returns>True when the arguments select command-line mode. <para>参数选择命令行模式时为 true。</para></returns>
    public static bool IsCommandLineMode(IReadOnlyList<string> args)
        => args.Count > 0 && (string.Equals(args[0], "--cli", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[0], "cli", StringComparison.OrdinalIgnoreCase)
            || args[0] is "version" or "firmware" or "hdc" or "adb" or "fastboot" or "magiskboot"
                or "delivery" or "project");

    /// <summary>
    /// Removes the optional mode switch while preserving command arguments.
    /// <para>移除可选的模式开关，同时保留其余命令参数。</para>
    /// </summary>
    /// <param name="args">Command-line arguments. <para>命令行参数。</para></param>
    /// <returns>The arguments without a leading mode switch, unchanged otherwise. <para>去掉开头模式开关后的参数；无开关时原样返回。</para></returns>
    public static string[] StripModeSwitch(string[] args)
        => args.Length > 0 && (string.Equals(args[0], "--cli", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[0], "cli", StringComparison.OrdinalIgnoreCase)) ? args[1..] : args;

    private static RootCommand CreateRootCommand(TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var root = new RootCommand("UotanStudio firmware and device command line interface.");
        root.SetAction(async parseResult =>
        {
            await output.WriteLineAsync("Use --help to list available commands.").ConfigureAwait(false);
            return 2;
        });

        var version = new Command("version", "Print the UotanStudio CLI version.");
        version.SetAction(parseResult =>
        {
            output.WriteLine(typeof(CliProgram).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return Task.FromResult(0);
        });
        root.Add(version);

        var firmware = new Command("firmware", "Inspect firmware files using the shared format registry.");
        var identify = new Command("identify", "Identify one firmware file.");
        var input = new Argument<FileInfo>("input");
        identify.Add(input);
        identify.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(input);
            if (file is null || !file.Exists)
            {
                error.WriteLine("Input file does not exist.");
                return Task.FromResult(3);
            }

            FirmwareFormatRegistry.RegisterAll();
            var handler = FormatDetector.Detect(file.FullName);
            output.WriteLine(handler?.FormatName ?? "Unknown");
            return Task.FromResult(handler is null ? 3 : 0);
        });
        firmware.Add(identify);
        var unpack = new Command("unpack", "Extract a recognized firmware container to a directory.");
        var unpackInput = new Argument<FileInfo>("input");
        var unpackOutput = new Option<DirectoryInfo>("--output") { Description = "Destination directory." };
        unpack.Add(unpackInput);
        unpack.Add(unpackOutput);
        unpack.SetAction(async parseResult =>
        {
            try
            {
                var file = parseResult.GetValue(unpackInput);
                var directory = parseResult.GetValue(unpackOutput);
                if (file is null || !file.Exists || directory is null) return 3;
                FirmwareFormatRegistry.RegisterAll();
                var handler = FormatDetector.Detect(file.FullName);
                if (handler is null) return 3;
                Directory.CreateDirectory(directory.FullName);
                var result = await handler.UnpackAsync(file.FullName, directory.FullName, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(result.Summary).ConfigureAwait(false);
                return result.Success ? 0 : 3;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        firmware.Add(unpack);
        root.Add(firmware);
        FirmwareCommands.Add(root, output, error, cancellationToken);

        var hdc = new Command("hdc", "Execute native OpenHarmony HDC protocol operations.");
        var targets = new Command("targets", "List targets from the HDC host channel.");
        var json = new Option<bool>("--json") { Description = "Emit machine-readable JSON." };
        targets.Add(json);
        targets.SetAction(async parseResult =>
        {
            try
            {
                using var client = new HdcServerClient();
                var details = client.ListTargetDetails();
                if (parseResult.GetValue(json))
                {
                    await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(details, CliJsonContext.Default.IReadOnlyListHdcTargetInfo)).ConfigureAwait(false);
                }
                else
                {
                    foreach (var target in details)
                    {
                        await output.WriteLineAsync($"{target.ConnectKey}\t{target.State}\t{target.Transport}\t{target.Name}").ConfigureAwait(false);
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 4;
            }
        });
        hdc.Add(targets);

        var shell = new Command("shell", "Run a command through the native HDC shell channel.");
        var serial = new Option<string?>("--serial") { Description = "HDC connect key. Defaults to the only target." };
        var command = new Argument<string[]>("command") { Arity = ArgumentArity.OneOrMore };
        shell.Add(serial);
        shell.Add(command);
        shell.SetAction(async parseResult =>
        {
            try
            {
                using var client = new HdcServerClient();
                var key = parseResult.GetValue(serial);
                if (string.IsNullOrWhiteSpace(key))
                {
                    var keys = client.ListTargets();
                    if (keys.Count != 1) throw new InvalidOperationException("Specify --serial when HDC has zero or multiple targets.");
                    key = keys[0];
                }

                using var session = client.CreateSession(key);
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                var result = session.CreateShellClient(string.Join(' ', parseResult.GetValue(command) ?? []))
                    .Execute();
                await output.WriteAsync(System.Text.Encoding.UTF8.GetString(result.Stdout)).ConfigureAwait(false);
                if (result.Stderr.Length > 0) await error.WriteAsync(System.Text.Encoding.UTF8.GetString(result.Stderr)).ConfigureAwait(false);
                return result.ExitCode < 0 ? 0 : result.ExitCode;
            }
            catch (OperationCanceledException)
            {
                return 7;
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 4;
            }
        });
        hdc.Add(shell);

        var prop = new Command("getprop", "Read an OpenHarmony system parameter through native HDC.");
        var propSerial = new Option<string?>("--serial") { Description = "HDC connect key. Defaults to the only target." };
        var propName = new Argument<string>("name");
        prop.Add(propSerial);
        prop.Add(propName);
        prop.SetAction(async parseResult =>
        {
            try
            {
                using var client = new HdcServerClient();
                var key = parseResult.GetValue(propSerial);
                if (string.IsNullOrWhiteSpace(key))
                {
                    var keys = client.ListTargets();
                    if (keys.Count != 1) throw new InvalidOperationException("Specify --serial when HDC has zero or multiple targets.");
                    key = keys[0];
                }

                using var session = client.CreateSession(key);
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(session.CreateDeviceServices().GetProp(parseResult.GetValue(propName)!)).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 7;
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 4;
            }
        });
        hdc.Add(prop);
        root.Add(hdc);

        var adb = new Command("adb", "Android Debug Bridge compatible device operations.");
        var adbDevices = new Command("devices", "List USB devices exposing the ADB interface.");
        adbDevices.SetAction(async _ =>
        {
            try
            {
                var devices = await CommKit.Default.EnumerateUsbDevicesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var device in devices.Where(static d => d.Interfaces.Any(i => i.Class == 0xff && i.SubClass == 0x42 && i.Protocol == 1)
                    || d.InterfaceClass == 0xff && d.InterfaceSubClass == 0x42 && d.InterfaceProtocol == 1))
                {
                    await output.WriteLineAsync($"{device.SerialNumber ?? device.DeviceKey}\tdevice").ConfigureAwait(false);
                }
                return 0;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 4; }
        });
        adb.Add(adbDevices);
        root.Add(adb);
        var fastboot = new Command("fastboot", "Execute native fastboot protocol operations.");
        var getvar = new Command("getvar", "Read one variable from the first matching fastboot USB target.");
        var variable = new Argument<string>("name");
        getvar.Add(variable);
        getvar.SetAction(async parseResult =>
        {
            try
            {
                using var devices = CommKit.Default.OpenUsbDeviceSessions();
                var factory = new FastbootFactory();
                foreach (var device in devices.Sessions)
                {
                    if (!factory.TryCreate(device, out var protocol) || protocol is not FastbootSession session) continue;
                    using (session)
                    {
                        var value = session.GetVariable(parseResult.GetValue(variable)!);
                        await output.WriteLineAsync($"{parseResult.GetValue(variable)}: {value}").ConfigureAwait(false);
                        return 0;
                    }
                }

                await error.WriteLineAsync("No fastboot target found.").ConfigureAwait(false);
                return 4;
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 4;
            }
        });
        fastboot.Add(getvar);
        root.Add(fastboot);
        var magiskboot = new Command("magiskboot", "MagiskBoot style boot image operations backed by the firmware handlers.");
        var magiskUnpack = new Command("unpack", "Unpack a boot/vendor_boot/init_boot image.");
        var magiskInput = new Argument<FileInfo>("input");
        var magiskOutput = new Option<DirectoryInfo>("--output") { Description = "Destination directory." };
        magiskUnpack.Add(magiskInput);
        magiskUnpack.Add(magiskOutput);
        magiskUnpack.SetAction(async parseResult =>
        {
            try
            {
                var file = parseResult.GetValue(magiskInput);
                var directory = parseResult.GetValue(magiskOutput);
                if (file is null || !file.Exists || directory is null) return 3;
                FirmwareFormatRegistry.RegisterAll();
                var handler = FormatDetector.Detect(file.FullName);
                if (handler is null || !handler.FormatName.Contains("Boot", StringComparison.OrdinalIgnoreCase)) return 3;
                Directory.CreateDirectory(directory.FullName);
                var result = await handler.UnpackAsync(file.FullName, directory.FullName, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(result.Summary).ConfigureAwait(false);
                return result.Success ? 0 : 3;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        magiskboot.Add(magiskUnpack);
        var magiskRepack = new Command("repack", "Rebuild a boot/vendor_boot/init_boot image from an unpacked directory.");
        var repackDirectory = new Argument<DirectoryInfo>("directory") { Description = "Directory produced by 'magiskboot unpack'." };
        var repackOutput = new Argument<FileInfo>("output") { Description = "Destination boot image path." };
        magiskRepack.Add(repackDirectory);
        magiskRepack.Add(repackOutput);
        magiskRepack.SetAction(async parseResult =>
        {
            try
            {
                var directory = parseResult.GetValue(repackDirectory);
                var file = parseResult.GetValue(repackOutput);
                if (directory is null || !directory.Exists || file is null) return 3;
                var metadata = RepackMetadata.Read(directory.FullName);
                if (metadata is null
                    || metadata.FormatName != new BootImageFormatHandler().FormatName
                    || !metadata.CanPackDirectory)
                {
                    await error.WriteLineAsync(
                        "Input directory is not a Boot repack tree or the source image cannot be rebuilt (for example, an AVB footer).")
                        .ConfigureAwait(false);
                    return 3;
                }
                FirmwareFormatRegistry.RegisterAll();
                var result = await new BootImageFormatHandler()
                    .PackAsync(directory.FullName, file.FullName, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(result.Summary).ConfigureAwait(false);
                return result.Success ? 0 : 3;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        magiskboot.Add(magiskRepack);
        var magiskCpio = new Command("cpio", "Inspect or extract a ramdisk CPIO archive.");
        AddUnavailable(magiskCpio, ["list", "extract", "add", "remove", "patch"], error);
        magiskboot.Add(magiskCpio);
        AddUnavailable(magiskboot, ["hexpatch", "cleanup"], error);
        root.Add(magiskboot);
        AddUnavailable(adb, ["shell", "pull", "push", "install", "reboot", "logcat"], error);
        AddUnavailable(fastboot, ["devices", "flash", "erase", "reboot", "boot", "fetch", "upload", "flashing", "oem"], error);
        root.Add(CreateDeliveryCommand(output, error, cancellationToken));
        root.Add(CreateProjectCommand(output, error, cancellationToken));
        return root;
    }

    /// <summary>
    /// Builds the <c>delivery</c> command group (validate / build / prepare-tools) on top of the public
    /// <see cref="DeliveryService"/> facade, driven by a serialized delivery-plan JSON file.
    /// <para>基于公共 <see cref="DeliveryService"/> 门面构建 <c>delivery</c> 命令组（validate / build / prepare-tools），
    /// 由序列化的交付计划 JSON 文件驱动。</para>
    /// </summary>
    private static Command CreateDeliveryCommand(TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var delivery = new Command("delivery", "Validate and build delivery plans from a plan JSON file.");

        var validate = new Command("validate", "Validate a delivery plan JSON file without building anything.");
        var validatePlan = new Argument<FileInfo>("plan") { Description = "Path to a DeliveryPlan JSON file." };
        var skipSources = new Option<bool>("--skip-sources") { Description = "Skip source-image existence checks." };
        validate.Add(validatePlan);
        validate.Add(skipSources);
        validate.SetAction(async parseResult =>
        {
            try
            {
                var file = parseResult.GetValue(validatePlan);
                if (file is null || !file.Exists) { await error.WriteLineAsync("Plan file not found.").ConfigureAwait(false); return 3; }
                var json = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                var message = DeliveryService.Validate(json, checkSources: !parseResult.GetValue(skipSources));
                if (message is null) { await output.WriteLineAsync("Delivery plan is valid.").ConfigureAwait(false); return 0; }
                await error.WriteLineAsync(message).ConfigureAwait(false);
                return 4;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        delivery.Add(validate);

        var build = new Command("build", "Build the delivery output from a validated plan; downloads platform tools (requires network).");
        var buildPlan = new Argument<FileInfo>("plan") { Description = "Path to a DeliveryPlan JSON file." };
        var overwrite = new Option<string[]>("--overwrite") { Description = "Existing output file names allowed to be replaced (default: none)." };
        var toolsCache = new Option<string?>("--tools-cache") { Description = "Directory caching downloaded platform tools." };
        build.Add(buildPlan);
        build.Add(overwrite);
        build.Add(toolsCache);
        build.SetAction(async parseResult =>
        {
            var file = parseResult.GetValue(buildPlan);
            if (file is null || !file.Exists) { await error.WriteLineAsync("Plan file not found.").ConfigureAwait(false); return 3; }
            try
            {
                var json = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                var names = parseResult.GetValue(overwrite);
                using var http = new HttpClient();
                var service = new DeliveryService(http, parseResult.GetValue(toolsCache));
                var progress = new Progress<string>(line => output.WriteLine(line));
                var outputs = await service.BuildAsync(json,
                    names is { Length: > 0 } ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase) : null,
                    progress, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Delivery build produced {outputs.Count} output path(s).").ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        delivery.Add(build);

        var prepare = new Command("prepare-tools", "Prepare platform-tools and zstd into a directory; requires network.");
        var preparePlan = new Argument<FileInfo>("plan") { Description = "Path to a DeliveryPlan JSON file." };
        var prepareDirectory = new Option<string?>("--directory") { Description = "Target directory (defaults to the plan's output directory)." };
        var prepareCache = new Option<string?>("--tools-cache") { Description = "Directory caching downloaded platform tools." };
        prepare.Add(preparePlan);
        prepare.Add(prepareDirectory);
        prepare.Add(prepareCache);
        prepare.SetAction(async parseResult =>
        {
            var file = parseResult.GetValue(preparePlan);
            if (file is null || !file.Exists) { await error.WriteLineAsync("Plan file not found.").ConfigureAwait(false); return 3; }
            try
            {
                var json = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                using var http = new HttpClient();
                var service = new DeliveryService(http, parseResult.GetValue(prepareCache));
                var progress = new Progress<string>(line => output.WriteLine(line));
                await service.PrepareToolsAsync(json, parseResult.GetValue(prepareDirectory), progress, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync("Platform tools prepared.").ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        delivery.Add(prepare);

        return delivery;
    }

    /// <summary>
    /// Builds the <c>project</c> command group (inspect / archive / extract) on top of the public
    /// <see cref="UotanStudio.Services.Projects"/> persistence and packaging services.
    /// <para>基于公共的 <see cref="UotanStudio.Services.Projects"/> 持久化与打包服务构建
    /// <c>project</c> 命令组（inspect / archive / extract）。</para>
    /// </summary>
    private static Command CreateProjectCommand(TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var project = new Command("project", "Inspect, archive or extract UotanStudio project files.");

        var inspect = new Command("inspect", "Inspect a .uotan solution or .uotanfw firmware project file.");
        var inspectTarget = new Argument<FileInfo>("path") { Description = "Path to a .uotan or .uotanfw file." };
        inspect.Add(inspectTarget);
        inspect.SetAction(async parseResult =>
        {
            var file = parseResult.GetValue(inspectTarget);
            if (file is null || !file.Exists) { await error.WriteLineAsync("Project file not found.").ConfigureAwait(false); return 3; }
            try
            {
                if (string.Equals(file.Extension, ".uotanfw", StringComparison.OrdinalIgnoreCase))
                {
                    var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                    if (firmware is null) { await error.WriteLineAsync("Cannot read the firmware project.").ConfigureAwait(false); return 3; }
                    await output.WriteLineAsync($"Firmware project: {firmware.ProjectName}").ConfigureAwait(false);
                    await output.WriteLineAsync($"  Images: {firmware.Images.Count}").ConfigureAwait(false);
                    foreach (var image in firmware.Images)
                        await output.WriteLineAsync($"    {image.RelativePath}  [{image.Format}]  {image.SizeBytes} bytes").ConfigureAwait(false);
                    if (firmware.Settings is { } settings)
                        await output.WriteLineAsync($"  ImageType: {settings.ImageType}  ImageFormat: {settings.ImageFormat}").ConfigureAwait(false);
                    return 0;
                }

                var solution = await SolutionPersistenceService.LoadSolutionAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                if (solution is null) { await error.WriteLineAsync("Cannot read the solution.").ConfigureAwait(false); return 3; }
                var subProjects = solution.SubProjects ?? [];
                await output.WriteLineAsync($"Solution: {solution.SolutionName}  ({subProjects.Count} sub-project(s))").ConfigureAwait(false);
                var root = file.DirectoryName ?? ".";
                foreach (var sub in subProjects)
                {
                    var path = Path.GetFullPath(Path.Combine(root, sub.RelativePath));
                    var firmware = File.Exists(path)
                        ? await SolutionPersistenceService.LoadFirmwareProjectAsync(path, cancellationToken).ConfigureAwait(false)
                        : null;
                    await output.WriteLineAsync($"  {sub.Name}  [{Path.GetFileName(sub.RelativePath)}]  images: {firmware?.Images.Count ?? -1}").ConfigureAwait(false);
                }
                return 0;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        project.Add(inspect);

        var archive = new Command("archive", "Export a .uotan solution into a portable package archive.");
        var archiveSolution = new Argument<FileInfo>("solution") { Description = "Path to the .uotan solution file." };
        var archiveOutput = new Argument<FileInfo>("output") { Description = "Destination package path (must be outside the project)." };
        archive.Add(archiveSolution);
        archive.Add(archiveOutput);
        archive.SetAction(async parseResult =>
        {
            var solution = parseResult.GetValue(archiveSolution);
            var destination = parseResult.GetValue(archiveOutput);
            if (solution is null || !solution.Exists || destination is null) { await error.WriteLineAsync("Solution file not found or output missing.").ConfigureAwait(false); return 3; }
            try
            {
                await ProjectArchiveService.ExportPackageAsync(solution.FullName, destination.FullName, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Archived to {destination.FullName}").ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        project.Add(archive);

        var extract = new Command("extract", "Import a project package archive into a new destination directory.");
        var extractPackage = new Argument<FileInfo>("package") { Description = "Path to the package archive." };
        var extractDestination = new Argument<string>("destination") { Description = "New destination directory (must not already exist)." };
        extract.Add(extractPackage);
        extract.Add(extractDestination);
        extract.SetAction(async parseResult =>
        {
            var package = parseResult.GetValue(extractPackage);
            var destination = parseResult.GetValue(extractDestination);
            if (package is null || !package.Exists || string.IsNullOrWhiteSpace(destination)) { await error.WriteLineAsync("Package not found or destination missing.").ConfigureAwait(false); return 3; }
            try
            {
                var solutionPath = await ProjectPackage.ImportAsync(package.FullName, destination, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Extracted solution to {solutionPath}").ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) { return 7; }
            catch (Exception ex) { await error.WriteLineAsync(ex.Message).ConfigureAwait(false); return 3; }
        });
        project.Add(extract);

        return project;
    }

    private static void AddUnavailable(Command parent, string[] names, TextWriter error)
    {
        foreach (var name in names)
        {
            var command = new Command(name, $"{parent.Name} {name} operation.");
            command.SetAction(async _ =>
            {
                await error.WriteLineAsync($"The {parent.Name} {name} operation is not available in this build.").ConfigureAwait(false);
                return 9;
            });
            parent.Add(command);
        }
    }
}

[JsonSerializable(typeof(IReadOnlyList<HdcTargetInfo>))]
[JsonSerializable(typeof(FirmwareInfoJson))]
[JsonSerializable(typeof(AvbVerifyJson))]
internal partial class CliJsonContext : JsonSerializerContext;