using System.Buffers.Binary;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Tools.UnisocProbe;

// Standalone, read-only diagnostic and backup host for Unisoc (SPRD) BSL download devices.
// This tool is deliberately outside the product solution; it never writes to the device.
internal static class Program
{
    private static readonly TimeSpan DefaultProbeBudget = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        // Unisoc handshake diagnostics use UsbTrace.LogErrorFormatted, which is always emitted. Keep the
        // general USB/Comm verbose traces off so the arrival window is not spent formatting enumerations.
        UotanStudio.Comm.Diagnostics.CommTrace.Level = UotanStudio.Comm.Diagnostics.CommLogLevel.Error;
        UotanStudio.Comm.Diagnostics.UsbTrace.IsEnabled = false;
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "ports" => ListPorts(),
                "pac-check" => await RunPacCheckAsync(args[1..]),
                "watch" => OperatingSystem.IsWindows()
                    ? await RunWatchAsync(args[1..])
                    : Fail("watch is Windows-only."),
                "attach" or "attach-partitions" => await RunAttachAsync(args[1..]),
                "raw" => await RunRawAsync(args[1..]),
                "discover" => await RunDiscoverAsync(args[1..]),
                "partitions" or "partition-list" => await RunPartitionsAsync(args[1..]),
                "backup" => await RunBackupAsync(args[1..]),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Canceled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ERROR: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            unisoc-probe — read-only Unisoc BSL diagnostics and full-partition backup

            Usage:
              unisoc-probe ports
              unisoc-probe pac-check --pac <path.pac>
              unisoc-probe watch --pac <path.pac> [--out <dir>] [--backup] [--exclude a,b] [--only a,b]
                                  [--nv include-with-confirm] [--timeout-ms N]  (no timeout by default)
              unisoc-probe attach --com <COMx> --out <dir> [--backup] [--nv include-with-confirm]
              unisoc-probe raw --com <COMx> [--baud 115200] [--timeout-ms 10000]
              unisoc-probe discover [--com <COMx>] [--timeout-ms 10000]
              unisoc-probe partitions --com <COMx> [--pac <path.pac>] [--timeout-ms 10000]
              unisoc-probe backup --com <COMx> --pac <path.pac> --out <dir> [--timeout-ms 10000]
                                  [--nv include-with-confirm] [--include-nv-names name1,name2]

            Modes:
              ports       List available serial ports.
              pac-check   Validate a PAC offline: CRC, loader roles/addresses and declared partitions.
              watch       Start first, then plug in the device; catches the 2-second U2S window.
              raw         Send one CHECK_BAUD byte and dump raw reply bytes as hex (no session, no writes).
              discover    Use the production discovery/arrival path and report the handshake stage.
              partitions  Bring the link to FDL2 (using --pac loaders when present) and enumerate device partitions.
              backup      Back up every safe partition to <dir>, with a SHA-256 manifest and atomic temp files.
                          NV/fixnv/runtimenv/calinv partitions are skipped by default; they can hang FDL2 and
                          destroy the session. Bring-up uses the PAC FDL1/FDL2 loaders when --pac is supplied.

            All modes are read-only: no write, erase, repartition, reboot or power-off commands are issued.
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int ListPorts()
    {
        Console.WriteLine("Available serial ports:");
        foreach (var port in SerialTransport.GetAvailablePorts())
        {
            Console.WriteLine($"  {port}");
        }

        return 0;
    }

    private static string GetOption(string[] args, string name, string? fallback = null)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return fallback ?? throw new ArgumentException($"Missing required option {name}.");
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    // The official spd_dump auto-enables 921600 when FDL1 loads at 0x5500 (the iPlay40's address).
    // BootROM CHECK_BAUD always stays at 115200; this only affects post-FDL1 transfers.
    private static int TransferBaudRate(string[] args)
        => HasFlag(args, "--high-speed") ? 921600 : 0;


    // Restricts the run to an explicit partition allow-list; empty means "every resolved partition".
    private static HashSet<string> OnlyPartitions(string[] args)
    {
        var values = GetOption(args, "--only", string.Empty);
        return values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExcludedPartitions(string[] args)
    {
        var values = GetOption(args, "--exclude", string.Empty);
        return values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int GetTimeoutMs(string[] args)
    {
        var value = GetOption(args, "--timeout-ms", "10000");
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) || timeout < 100)
        {
            throw new ArgumentException($"Invalid --timeout-ms '{value}'.");
        }

        return timeout;
    }


    private static async Task<int> RunPacCheckAsync(string[] args)
    {
        var pac = GetOption(args, "--pac");
        Console.WriteLine($"Validating PAC: {pac}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = UnisocFlashPlan.Load(pac, CancellationToken.None);
        try
        {
            sw.Stop();
            Console.WriteLine($"PAC valid in {sw.ElapsedMilliseconds} ms.");
            Console.WriteLine($"Loaders: FDL1 id={plan.Loaders.FirstStage.Id}, size={plan.Loaders.FirstStage.Length}, address=0x{plan.Loaders.FirstStageAddress:X8}");
            Console.WriteLine($"         FDL2 id={plan.Loaders.SecondStage.Id}, size={plan.Loaders.SecondStage.Length}, address=0x{plan.Loaders.SecondStageAddress:X8}");
            Console.WriteLine($"Images: {plan.Images.Count}; declared partitions: {plan.Partitions.Count}; ExtTable: {(plan.ExtTable is null ? "none" : plan.ExtTable.Length + " bytes")}");
            foreach (var partition in plan.Partitions)
            {
                var mb = partition.fillsRemainingSpace ? "fills-remaining" : $"{partition.size / (1024.0 * 1024.0):F1} MiB";
                Console.WriteLine($"  {partition.id,-24} {mb}");
            }
            return 0;
        }
        finally
        {
            plan.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWatchAsync(string[] args)
    {
        var pac = GetOption(args, "--pac");
        var outDir = GetOption(args, "--out", string.Empty);
        var doBackup = HasFlag(args, "--backup");
        var nvConfirmed = GetOption(args, "--nv", string.Empty)
            .Equals("include-with-confirm", StringComparison.OrdinalIgnoreCase);
        var explicitNv = GetOption(args, "--include-nv-names", string.Empty);
        // No timeout by default: the watcher waits for the device indefinitely. Pass --timeout-ms only when
        // a bounded wait is genuinely wanted.
        var budgetText = GetOption(args, "--timeout-ms", string.Empty);
        int? budget = null;
        if (!string.IsNullOrWhiteSpace(budgetText))
        {
            if (!int.TryParse(budgetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1000)
            {
                throw new ArgumentException($"Invalid --timeout-ms '{budgetText}'.");
            }

            budget = parsed;
        }

        Console.WriteLine("WATCH mode: edge-triggered on a NEW USB\\VID_1782&PID_4D00 COM port.");
        // Validate the PAC's CRC and loaders BEFORE arming: the 4.5 GB check takes ~20 s, far too long to
        // run inside the device's arrival window.
        Console.WriteLine("Validating PAC before arming the watcher...");
        var plan = UnisocFlashPlan.Load(pac, CancellationToken.None);
        plan.Dispose();
        Console.WriteLine("PAC valid.");
        if (doBackup)
        {
            if (string.IsNullOrWhiteSpace(outDir))
            {
                throw new ArgumentException("--out is required with --backup.");
            }

            var fullOut = Path.GetFullPath(outDir);
            Console.WriteLine($"Backup destination: '{outDir}' -> '{fullOut}' (cwd={Directory.GetCurrentDirectory()})");
            try
            {
                Directory.CreateDirectory(fullOut);
                Console.WriteLine("  CreateDirectory OK.");
                var probeFile = Path.Combine(fullOut, $".write-probe-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(probeFile, "ok").ConfigureAwait(false);
                Console.WriteLine("  Write OK: " + probeFile);
                File.Delete(probeFile);
                Console.WriteLine("  Delete OK.");
            }
            catch (Exception preflightError)
            {
                throw new IOException($"Backup destination '{fullOut}' is not writable: {preflightError}", preflightError);
            }

            Console.WriteLine($"Backup destination verified writable: {fullOut}");
        }

        Console.WriteLine("Building controller...");
        // Build the controller BEFORE the device arrives: its constructor probes adb/HDC servers and
        // takes seconds. Doing this at arrival time would consume the whole 2-second U2S window.
        using var controller = DeviceControllerFactory.CreateForDirectSerial();

        // Baseline of currently-live SPRD ports. An already-connected BootROM answers CHECK_BAUD once,
        // then stays enumerated but deaf; firing on a port in this baseline can only get silence.
        var previous = LiveUnisocPorts();
        if (previous.Count > 0)
        {
            Console.WriteLine($"Baseline SPRD port(s) already live: {string.Join(", ", previous)}.");
            Console.WriteLine("If the device is currently connected, UNPLUG it now — the probe only fires on a fresh 0->1 edge.");
        }

        Console.WriteLine("Probe armed. Plug the device into download mode now.");
        using var cts = budget is { } bounded
            ? new CancellationTokenSource(bounded)
            : new CancellationTokenSource();
        while (!cts.IsCancellationRequested)
        {
            var current = LiveUnisocPorts();
            var appeared = current.Except(previous, StringComparer.OrdinalIgnoreCase).ToArray();
            previous = current;
            if (appeared.Length > 0)
            {
                var port = appeared[0];
                Console.WriteLine($"EDGE: SPRD port {port} just appeared; handshaking now.");
                // Do NOT probe the port before the handshake: the BootROM answers CHECK_BAUD once, and a
                // diagnostic open would consume the only answer before the session can read it. The session's
                // own CHECK_BAUD is the single probe. Raw capture stays available as a standalone command.
                // The BootROM answers CHECK_BAUD only once and only for ~2 s; roughly half of insertions
                // miss that window. A failed attempt must NOT end the watcher: keep the baseline updated and
                // wait for the next 0->1 edge (the user simply replugs).
                try
                {
                    // The watch budget must not bound the backup: a full 64 GB read takes far longer. Hand the
                    // work a fresh token so only Ctrl+C / the caller can stop a transfer in progress.
                    return await HandleArrivalAsync(controller, port, pac, outDir, doBackup, nvConfirmed, explicitNv,
                        CancellationToken.None, ExcludedPartitions(args), OnlyPartitions(args)).ConfigureAwait(false);
                }
                catch (Exception arrivalError)
                {
                    Console.WriteLine($"Arrival attempt failed ({arrivalError.GetType().Name}: {arrivalError.Message}).");
                    Console.WriteLine("Waiting for the next unplug/replug edge; no restart needed.");
                    continue;
                }
            }

            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        Console.Error.WriteLine("Watch timed out with no new Unisoc download port edge.");
        return 124;
    }

    private sealed record UnisocPort(string InstanceId, string PortName);

    [SupportedOSPlatform("windows")]
    private static HashSet<string> LiveUnisocPorts()
    {
        var live = SerialTransport.GetAvailablePorts().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ReadUnisocPorts()
            .Select(p => p.PortName)
            .Where(live.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<UnisocPort> ReadUnisocPorts()
    {
        var result = new List<UnisocPort>();
        using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1782&PID_4D00");
        if (baseKey is null)
        {
            return result;
        }

        foreach (var instance in baseKey.GetSubKeyNames())
        {
            using var deviceKey = baseKey.OpenSubKey(instance);
            var portName = deviceKey?.GetValue("FriendlyName") as string;
            if (portName is null)
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(portName, @"\((COM\d+)\)");
            if (match.Success)
            {
                result.Add(new UnisocPort(instance, match.Groups[1].Value));
            }
        }

        return result;
    }


    private static async Task RawCaptureOnceAsync(string com, CancellationToken cancellationToken)
    {
        try
        {
            using var transport = new SerialTransport(com, 115200, Parity.None, 8, StopBits.One, Handshake.None);
            transport.Connect();
            transport.Reset();
            var probe = new byte[] { 0x7E };
            await transport.WriteAsync(probe, 0, 1, 2000, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[2048];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(1500);
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await transport.ReadAsync(buffer, total, buffer.Length - total, 500, readCts.Token)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (ContainsHeader(buffer.AsSpan(0, total), 2))
                {
                    break;
                }
            }

            Console.WriteLine($"RAW REPLY ({total} bytes): {Convert.ToHexString(buffer.AsSpan(0, total))}");
            if (total > 0)
            {
                Console.WriteLine("  ASCII: " + System.Text.Encoding.ASCII.GetString(buffer.AsSpan(0, total)));
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"RAW CAPTURE failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private static async Task<int> HandleArrivalAsync(DeviceController controller, string com, string pac,
        string outDir, bool doBackup, bool nvConfirmed, string explicitNv, CancellationToken cancellationToken,
        HashSet<string>? excluded = null, HashSet<string>? only = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"Triggering IMMEDIATE handshake on {com} inside the 2-second window.");
        // Handshake uses only the caller's token: FDL uploads and a full backup legitimately run for minutes.
        // Open the exact named COM port directly: no discovery walk, no PnP-path resolution.
        // This is the only path fast enough for the ~2-second U2S window.
        var lease = await controller.HandshakeUnisocSerialPortAsync(com, cancellationToken: cancellationToken,
            storageBlockSize: 0xF800).ConfigureAwait(false);
        if (lease is null)
        {
            throw new IOException($"Could not open {com} after {sw.ElapsedMilliseconds} ms; the 2-second window may already have closed.");
        }

        await using (lease)
        {
            Console.WriteLine($"Handshake attempt completed in {sw.ElapsedMilliseconds} ms.");
            var session = RequireSession(lease);
            Console.WriteLine($"Handshake OK: stage={session.Stage}, secure={session.Identity.SecureBoot}");
            if (!string.IsNullOrWhiteSpace(pac))
            {
                await BringUpFdl2Async(lease, pac, cancellationToken).ConfigureAwait(false);
            }

            if (!doBackup)
            {
                Console.WriteLine("Watch completed; no backup requested. Device remains at its current stage.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(outDir))
            {
                throw new ArgumentException("--out is required with --backup.");
            }

            await BackupWithLeaseAsync(lease, outDir, nvConfirmed, explicitNv, cancellationToken, pac, excluded, only).ConfigureAwait(false);
            return 0;
        }
    }


    private static async Task<int> RunAttachAsync(string[] args)
    {
        var com = GetOption(args, "--com");
        var outDir = GetOption(args, "--out", string.Empty);
        var doBackup = HasFlag(args, "--backup");
        var nvConfirmed = GetOption(args, "--nv", string.Empty)
            .Equals("include-with-confirm", StringComparison.OrdinalIgnoreCase);
        var explicitNv = GetOption(args, "--include-nv-names", string.Empty);

        using var controller = DeviceControllerFactory.CreateForDirectSerial();
        var lease = await controller.AttachUnisocFdl2Async(com).ConfigureAwait(false);
        if (lease is null)
        {
            throw new IOException($"Could not open {com} to attach to FDL2.");
        }

        await using (lease)
        {
            var session = RequireSession(lease);
            Console.WriteLine($"Attached: stage={session.Stage}");
            if (session.Stage != UnisocBootStage.Fdl2)
            {
                throw new IOException($"Expected a live FDL2 on {com}, got {session.Stage}.");
            }

            if (doBackup)
            {
                if (string.IsNullOrWhiteSpace(outDir))
                {
                    throw new ArgumentException("--out is required with --backup.");
                }

                await BackupWithLeaseAsync(lease, Path.GetFullPath(outDir), nvConfirmed, explicitNv).ConfigureAwait(false);
            }
            else
            {
                var targets = await new UnisocFlasher(session).ResolvePartitionsAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                Console.WriteLine($"Resolved {targets.Count} partition(s):");
                foreach (var target in targets)
                {
                    var size = target.Length is { } length ? length.ToString(CultureInfo.InvariantCulture)
                        : target.NativeSizeValue is { } raw ? $"native:{raw}" : "unknown";
                    Console.WriteLine($"  {target.Name,-28} {size}");
                }
            }
        }

        return 0;
    }

    private static async Task<int> RunRawAsync(string[] args)
    {
        var com = GetOption(args, "--com");
        var baudValue = GetOption(args, "--baud", "115200");
        if (!int.TryParse(baudValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud))
        {
            throw new ArgumentException($"Invalid --baud '{baudValue}'.");
        }

        var timeout = GetTimeoutMs(args);
        using var transport = new SerialTransport(com, baud, Parity.None, 8, StopBits.One, Handshake.None);
        transport.Connect();
        transport.Reset();
        Console.WriteLine($"Opened {com} at {baud} baud; clearing buffers and sending CHECK_BAUD (0x7E).");
        var probe = new byte[] { 0x7E };
        await transport.WriteAsync(probe, 0, probe.Length, timeout).ConfigureAwait(false);
        var buffer = new byte[4096];
        var total = 0;
        using var cts = new CancellationTokenSource(DefaultProbeBudget);
        while (total < buffer.Length)
        {
            var read = await transport.ReadAsync(buffer, total, buffer.Length - total, timeout, cts.Token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (ContainsHeader(buffer.AsSpan(0, total), 2))
            {
                break;
            }
        }

        Console.WriteLine($"Received {total} byte(s):");
        Console.WriteLine("  " + Convert.ToHexString(buffer.AsSpan(0, total)));
        if (HasFlag(args, "--verbose"))
        {
            Console.WriteLine("  ASCII: " + System.Text.Encoding.ASCII.GetString(buffer.AsSpan(0, total)));
        }

        return 0;
    }

    private static bool ContainsHeader(ReadOnlySpan<byte> data, int minimumFrames)
    {
        var headers = 0;
        foreach (var b in data)
        {
            if (b == 0x7E)
            {
                headers++;
            }
        }

        return headers >= minimumFrames;
    }

    private static async Task<int> RunDiscoverAsync(string[] args)
    {
        var com = GetOption(args, "--com", string.Empty);
        var timeout = GetTimeoutMs(args);
        using var cts = new CancellationTokenSource(timeout);
        var controller = DeviceControllerFactory.CreateDefault();
        await using (controller)
        {
            var snapshot = await controller.RefreshAsync(cts.Token).ConfigureAwait(false);
            Console.WriteLine($"Snapshot generation {snapshot.Generation}: {snapshot.Endpoints.Length} endpoint(s).");
            foreach (var endpoint in snapshot.Endpoints)
            {
                Console.WriteLine($"  {endpoint.Protocol ?? "(none)"} {endpoint.Address} [{endpoint.Transport}]");
            }

            var candidates = snapshot.Endpoints
                .Where(e => string.Equals(e.Protocol, "unisoc", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0)
            {
                Console.WriteLine("No Unisoc endpoints found in the inventory.");
                if (com.Length > 0)
                {
                    Console.WriteLine("--com was supplied: use 'raw' or 'partitions' for direct COM diagnostics.");
                }
            }

            foreach (var endpoint in candidates)
            {
                try
                {
                    await using var lease = await controller.OpenAsync(snapshot, endpoint.Id,
                        UotanStudio.Comm.Contracts.DeviceSessionKind.Unisoc, cts.Token).ConfigureAwait(false);
                    if (lease.TryGetSession<UnisocDeviceSession>(out var session))
                    {
                        var identity = session.Identity;
                        Console.WriteLine($"  {endpoint.Address}: stage={identity.Stage}, version={(identity.Version is null ? "(none)" : System.Text.Encoding.ASCII.GetString(identity.Version))}, secure={identity.SecureBoot}");
                    }
                    else
                    {
                        Console.WriteLine($"  {endpoint.Address}: opened but no Unisoc session.");
                    }
                }
                catch (Exception error)
                {
                    Console.WriteLine($"  {endpoint.Address}: OPEN FAILED {error.GetType().Name}: {error.Message}");
                }
            }
        }

        return 0;
    }

    private static async Task<int> RunPartitionsAsync(string[] args)
    {
        var com = GetOption(args, "--com");
        var pac = GetOption(args, "--pac", string.Empty);
        var timeout = GetTimeoutMs(args);
        using var cts = new CancellationTokenSource(timeout);
        await using var lease = await LeaseForComAsync(com, pac, cts.Token).ConfigureAwait(false);
        var session = RequireSession(lease);
        Console.WriteLine($"Stage: {session.Stage}");
        if (session.Stage != UnisocBootStage.Fdl2)
        {
            Console.WriteLine("Device is not at FDL2; supply --pac to load FDL1/FDL2, or use a device already at FDL2.");
            return 0;
        }

        var flasher = new UnisocFlasher(session);
        var targets = await flasher.ResolvePartitionsAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine($"Resolved {targets.Count} partition(s):");
        foreach (var target in targets)
        {
            var size = target.Length is { } length ? length.ToString(CultureInfo.InvariantCulture)
                : target.NativeSizeValue is { } raw ? $"native:{raw}" : "unknown";
            Console.WriteLine($"  {target.Name,-28} {size}");
        }

        return 0;
    }

    private static async Task<int> RunBackupAsync(string[] args)
    {
        var com = GetOption(args, "--com");
        var pac = GetOption(args, "--pac");
        var outDir = Path.GetFullPath(GetOption(args, "--out"));
        var timeout = GetTimeoutMs(args);
        var includeNv = GetOption(args, "--nv", string.Empty)
            .Equals("include-with-confirm", StringComparison.OrdinalIgnoreCase);
        var explicitNv = GetOption(args, "--include-nv-names", string.Empty);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMinutes(60));

        Directory.CreateDirectory(outDir);
        await using var lease = await LeaseForComAsync(com, pac, cts.Token).ConfigureAwait(false);
        await BackupWithLeaseAsync(lease, outDir, includeNv, explicitNv, cts.Token, pac).ConfigureAwait(false);
        return 0;
    }

    private static async Task BackupWithLeaseAsync(DeviceLease lease, string outDir, bool includeNv,
        string explicitNv, CancellationToken cancellationToken = default, string? pacPath = null,
        HashSet<string>? excluded = null, HashSet<string>? only = null)
    {
        excluded ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        only ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Both callers (backup and watch --backup) rely on this: a validated, writable destination must exist
        // before the manifest is opened, otherwise the whole FDL2 session is wasted on a path error.
        Directory.CreateDirectory(outDir);
        var session = RequireSession(lease);
        Console.WriteLine($"Stage: {session.Stage}");
        if (session.Stage != UnisocBootStage.Fdl2)
        {
            throw new IOException("Backup requires FDL2. Supply a valid --pac with FDL1/FDL2 loaders.");
        }

        var manifestPath = Path.Combine(outDir, "backup-manifest.tsv");
        await using var manifest = new StreamWriter(manifestPath, append: false);
        await manifest.WriteLineAsync("partition	bytes	sha256	status").ConfigureAwait(false);
        Dictionary<string, long> pacSizes = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(pacPath))
        {
            using var plan = UnisocFlashPlan.Load(pacPath, cancellationToken);
            foreach (var partition in plan.Partitions)
            {
                if (partition.size > 0 && !partition.fillsRemainingSpace)
                {
                    pacSizes[partition.id] = partition.size;
                }
            }

            Console.WriteLine($"PAC declares {pacSizes.Count} partition size(s).");
        }

        var storage = session.OpenStorage();
        var allTargets = await new UnisocFlasher(session).ResolvePartitionsAsync(cancellationToken).ConfigureAwait(false);
        // Back up small partitions first: a fault on one huge partition kills the FDL2 session, so the
        // remaining small partitions must never sit behind it in the queue.
        // fixnv-family reads are known to hang FDL2, and one faulted session fails everything after it, so
        // they go last: the other partitions are already on disk before the risky read starts.
        var targets = allTargets
            .Where(t => only.Count == 0 || only.Contains(t.Name))
            .OrderBy(t => IsFixNvPartition(t.Name) ? 2 : 0)
            .ThenBy(t => IsHugePartition(t.Name) && !HasFlagValue(explicitNv, t.Name) ? 1 : 0)
            .ThenBy(t => pacSizes.TryGetValue(t.Name, out var s) ? s : long.MaxValue)
            .ToArray();
        Console.WriteLine($"Resolved {targets.Length} partition(s) from the device.");
        var succeeded = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excluded.Contains(target.Name))
            {
                Console.WriteLine($"SKIP {target.Name}: excluded by --exclude.");
                await manifest.WriteLineAsync($"{target.Name}\t0\t-\tskipped-excluded").ConfigureAwait(false);
                continue;
            }

            var isNv = IsNvPartition(target.Name);
            var explicitlyIncluded = explicitNv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(target.Name, StringComparer.OrdinalIgnoreCase);
            if (isNv && !(includeNv || explicitlyIncluded))
            {
                Console.WriteLine($"SKIP {target.Name}: NV-family partition (use --nv include-with-confirm or --include-nv-names).");
                await manifest.WriteLineAsync($"{target.Name}	0	-	skipped-nv").ConfigureAwait(false);
                continue;
            }

            long length;
            long offset;
            if (target.Length is { } confirmed && confirmed > 0 && target.Offset is { } confirmedOffset)
            {
                length = confirmed;
                offset = confirmedOffset;
            }
            else
            {
                // The device returned a native table (name + device-defined size value, no byte extent).
                // Prefer the byte size declared by the PAC scheme; fall back to the vendor-style probe only when
                // the package has no entry for this partition.
                offset = 0;
                if (pacSizes.TryGetValue(target.Name, out var declared) && declared > 0)
                {
                    length = declared;
                    Console.WriteLine($"SIZE {target.Name} from PAC: {length} bytes.");
                }
                else
                {
                    Console.WriteLine($"PROBE SIZE {target.Name} (PAC has no size; native value {target.NativeSizeValue?.ToString() ?? "n/a"})...");
                    length = await storage.DiscoverPartitionSizeAsync(target.Name, cancellationToken).ConfigureAwait(false);
                    if (length <= 0)
                    {
                        Console.WriteLine($"SKIP {target.Name}: size probe returned zero (partition likely absent).");
                        await manifest.WriteLineAsync($"{target.Name}	0	-	skipped-zero-size").ConfigureAwait(false);
                        continue;
                    }
                }
            }

            var destination = Path.Combine(outDir, Sanitize(target.Name) + ".img");
            // Resume a partial temp from an interrupted run instead of restarting a 4 GB read from zero.
            var existingTemp = Directory.EnumerateFiles(outDir, $".{Sanitize(target.Name)}.*.tmp")
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            var temporary = existingTemp ?? Path.Combine(outDir, $".{Sanitize(target.Name)}.{Guid.NewGuid():N}.tmp");
            var alreadyRead = existingTemp is null ? 0 : new FileInfo(existingTemp).Length;
            // Resume: a completed image with the exact declared size was already SHA-256 verified when
            // written, so do not spend the fragile FDL2 session re-reading it.
            if (File.Exists(destination) && new FileInfo(destination).Length == length)
            {
                Console.WriteLine($"SKIP {target.Name}: already backed up ({length} bytes).");
                await manifest.WriteLineAsync($"{target.Name}\t{length}\t-\tresume-skipped").ConfigureAwait(false);
                succeeded++;
                continue;
            }

            try
            {
                Console.WriteLine($"READ {target.Name}: {length} bytes -> {destination}" +
                    (alreadyRead > 0 ? $" (resuming at {alreadyRead} bytes)" : string.Empty));
                using var digest = Sha256.CreateAccumulator();
                if (alreadyRead > 0)
                {
                    using var seed = File.OpenRead(temporary);
                    var buffer = new byte[64 * 1024];
                    int n;
                    while ((n = await seed.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        digest.Append(buffer.AsSpan(0, n));
                    }
                }

                var speed = new SpeedProgress($"{target.Name}", length);
                speed.Report(alreadyRead); // seed baseline; subsequent reports carry the absolute offset
                await using (var output = new FileStream(temporary, alreadyRead > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read))
                {
                    var remaining = length - alreadyRead;
                    var at = offset + alreadyRead;
                    while (remaining > 0)
                    {
                        // A single BSL read session covers at most the 32-bit range; split large partitions.
                        var chunkLength = (int)Math.Min(remaining, 0x7FFF_F000L);
                        await using var tee = new DigestSink(output, digest);
                        var read = await storage.ReadPartitionAsync(target.Name, at, chunkLength,
                            tee, cancellationToken, new OffsetProgress(at, speed)).ConfigureAwait(false);
                        if (read != chunkLength)
                        {
                            throw new IOException($"Short read: expected {chunkLength}, got {read} bytes.");
                        }

                        at += read;
                        remaining -= read;
                    }
                }

                var hash = digest.GetHashAndReset();
                File.Move(temporary, destination, overwrite: true);
                await manifest.WriteLineAsync($"{target.Name}	{length}	{Convert.ToHexStringLower(hash)}	ok")
                    .ConfigureAwait(false);
                succeeded++;
                Console.WriteLine($"  OK {target.Name}: sha256={Convert.ToHexStringLower(hash)}");
            }
            catch (Exception error)
            {
                // Keep any partial temp so a later run resumes instead of re-reading gigabytes from zero.
                var kept = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
                await manifest.WriteLineAsync($"{target.Name}	{kept}	-	failed:{error.GetType().Name}:{Escape(error.Message)}")
                    .ConfigureAwait(false);
                Console.WriteLine($"  FAILED {target.Name}: {error.GetType().Name}: {error.Message}" +
                    (kept > 0 ? $" (kept {kept / (1024.0 * 1024.0):F1} MB partial for resume)" : string.Empty));
                Console.Error.WriteLine("NOTE: a failed partition read may have faulted the FDL2 session; remaining partitions will likely fail too.");
            }
        }

        Console.WriteLine($"Backup complete: {succeeded}/{targets.Length} successful. Manifest: {manifestPath}");
    }

    private static async Task<DeviceLease> LeaseForComAsync(string com, string pac, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(com))
        {
            throw new ArgumentException("--com is required.");
        }

        // No background USB monitor: an incremental WinUSB probe on the same node disturbs the download
        // gadget. Open the named port directly instead, which also avoids a slow inventory walk.
        var controller = DeviceControllerFactory.CreateForDirectSerial();
        var lease = await controller.HandshakeUnisocSerialPortAsync(com, cancellationToken: cancellationToken,
            storageBlockSize: 0xF800).ConfigureAwait(false);
        if (lease is null)
        {
            throw new IOException($"Could not open {com}. Is the device still in download mode?");
        }

        Console.WriteLine($"Opened {com} directly via the no-watch controller.");

        if (!string.IsNullOrWhiteSpace(pac))
        {
            await BringUpFdl2Async(lease, pac, cancellationToken).ConfigureAwait(false);
        }

        return lease;
    }

    private static async Task BringUpFdl2Async(DeviceLease lease, string pacPath, CancellationToken cancellationToken)
    {
        var session = RequireSession(lease);
        var wasFdl2 = session.Stage == UnisocBootStage.Fdl2;
        if (!wasFdl2)
        {
        var plan = UnisocFlashPlan.Load(pacPath, cancellationToken);
        try
        {
            Console.WriteLine($"PAC: {plan.PackagePath}; loaders FDL1 @0x{plan.Loaders.FirstStageAddress:X8}, FDL2 @0x{plan.Loaders.SecondStageAddress:X8}");
            if (session.Stage == UnisocBootStage.BootRom)
            {
                await using var first = plan.OpenLoader(plan.Loaders.FirstStage);
                Console.WriteLine("Uploading FDL1...");
                await session.UploadFdl1Async(first, plan.Loaders.FirstStageAddress, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"FDL1 uploaded; stage={session.Stage}");
            }

            await using var second = plan.OpenLoader(plan.Loaders.SecondStage);
            Console.WriteLine("Uploading FDL2...");
            await session.UploadFdl2Async(second, plan.Loaders.SecondStageAddress, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"FDL2 uploaded; stage={session.Stage}");
        }
        finally
        {
            plan.Dispose();
        }
        }

        // The reference notes require an explicit disable_transcode at the start of every new FDL2
        // session: transcode state survives process exits, and reading binary partitions while the host
        // still escapes 0x7E causes "unexpected escaped byte" / malformed-frame faults.
        Console.WriteLine("Disabling transcode before partition reads...");
        await session.DisableTranscodeAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Transcode disabled.");
    }

    private static UnisocDeviceSession RequireSession(DeviceLease lease)
        => lease.TryGetSession<UnisocDeviceSession>(out var session)
            ? session
            : throw new IOException("The lease does not carry a Unisoc device session.");

    private static bool IsHugePartition(string name)
        => string.Equals(name, "super", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "userdata", StringComparison.OrdinalIgnoreCase);

    private static bool HasFlagValue(string names, string name)
        => names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsFixNvPartition(string name)
        => name.Contains("fixnv", StringComparison.OrdinalIgnoreCase);

    private static bool IsNvPartition(string name)
        => name.Contains("nv", StringComparison.OrdinalIgnoreCase)
           || name.Contains("fixnv", StringComparison.OrdinalIgnoreCase)
           || name.Contains("runtimenv", StringComparison.OrdinalIgnoreCase)
           || name.Contains("calinv", StringComparison.OrdinalIgnoreCase);



    // Reports cumulative bytes with live speed and ETA at a bounded cadence, so a long read has visible progress.

    private sealed class OffsetProgress(long baseOffset, IProgress<long> inner) : IProgress<long>
    {
        public void Report(long value) => inner.Report(baseOffset + value);
    }

    private sealed class SpeedProgress(string name, long total) : IProgress<long>
    {
        private long _lastBytes;
        private long _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        private static readonly long ReportIntervalTicks = System.Diagnostics.Stopwatch.Frequency * 10;

        public void Report(long value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastTicks < ReportIntervalTicks)
            {
                return;
            }

            var seconds = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            var speed = seconds > 0 ? (value - _lastBytes) / seconds : 0;
            var shown = Math.Min(value, total);
            var percent = total > 0 ? shown * 100.0 / total : 0;
            var remaining = speed > 0 ? (total - shown) / speed : double.NaN;
            var eta = double.IsNaN(remaining) ? "eta=?" : $"eta={TimeSpan.FromSeconds(Math.Max(0, remaining)).TotalHours:F2}h";
            Console.WriteLine($"  {name}: {value / (1024.0 * 1024.0):F1}/{total / (1024.0 * 1024.0):F1} MB ({percent:F1}%) " +
                $"@ {speed / (1024.0 * 1024.0):F2} MB/s, {eta}");
            _lastBytes = value;
            _lastTicks = now;
        }
    }

    // Wraps the destination stream to feed a running SHA-256 without reopening (which would lock the file).
    // Disposal deliberately leaves the wrapped stream open so the caller can continue chunked writes.
    private sealed class DigestSink(Stream target, UotanStudio.Hash.Digests.DigestAccumulator digest) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override Task FlushAsync(CancellationToken token) => target.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            digest.Append(buffer.AsSpan(offset, count));
            target.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            digest.Append(buffer.Span);
            return target.WriteAsync(buffer, token);
        }

        protected override void Dispose(bool disposing)
        {
            // Do not dispose the wrapped target: chunked writes reuse it across sink instances.
            base.Dispose(disposing);
        }
    }

    private static string Sanitize(string name)
    {
        var chars = name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray();
        return new string(chars).Trim();
    }

    private static string Escape(string message) => message.Replace("\t", " ").Replace("\n", " ").Replace('\t', ' ').Replace('\r', ' ');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; the manifest records the failure.
        }
    }
}
