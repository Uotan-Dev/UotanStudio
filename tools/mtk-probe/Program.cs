using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Tools.MtkProbe;

internal static class Program
{
    private static int _exitCode;

    private static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        ProbeOptions? options = null;
        try
        {
            options = ProbeOptions.Parse(args);
            if (options.ShowHelp)
            {
                ProbeOptions.PrintUsage();
                return 0;
            }

            if (options.ListLoaders)
            {
                DaDiscovery.List(default, Log);
                return 0;
            }

            if (options.ProbeIdentity is { } identity)
            {
                var probe = new MtkDeviceInfo(identity.Hw, identity.Sub, identity.Ver, 0, 0);
                var found = DaDiscovery.Find(probe, options.Loader is { } l ? [l] : DaDiscovery.DefaultLoaderCandidates(), Log);
                Log(found is null ? "RESULT: no match" : $"RESULT: {found.Description} " +
                    $"stage1={found.StageOneRegion} stage2={found.StageTwoRegion}");
                return found is null ? 2 : 0;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            deadline.CancelAfter(options.OverallTimeout);
            await RunAsync(options, deadline.Token).ConfigureAwait(false);
            return _exitCode;
        }
        catch (OperationCanceledException)
        {
            Log("cancelled; the device stays in whatever stage it reached.");
            return 3;
        }
        catch (Exception error)
        {
            Log($"FAILED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static async Task RunAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var session = await AcquireSessionAsync(options, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        using (session)
        {
            await DrainAsync(session, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<MtkSession?> AcquireSessionAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        // A stale COM name can still be opened after the device is unplugged, and another attached
        // device can answer on a port that never speaks the boot protocol. Rejecting those ports and
        // going back to polling keeps the whole wait budget available for the real preloader, whose
        // port only exists for a few seconds after attach.
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var port = await WaitForPortAsync(options, rejected, cancellationToken).ConfigureAwait(false);
            if (port is null)
            {
                Log(rejected.Count == 0
                    ? "no candidate port appeared before the wait budget expired."
                    : "no responsive preloader appeared before the wait budget expired; rejected " +
                      string.Join(", ", rejected) + ".");
                _exitCode = 2;
                return null;
            }

            Log($"port {port} present; opening now, inside the attach window.");
            var device = await IdentifyAsync(port, options, cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                rejected.Add(port);
                Log($"rejected {port} and resuming the wait for a responsive preloader.");
                continue;
            }

            var match = await ResolveLoaderAsync(device, options, cancellationToken).ConfigureAwait(false);
            if (match is null)
            {
                _exitCode = 1;
                return null;
            }

            var session = await BootstrapAsync(port, match, options, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                _exitCode = 1;
                return null;
            }

            return session;
        }
    }

    private static async Task DrainAsync(MtkSession session, ProbeOptions options, CancellationToken cancellationToken)
    {
        if (options.Scan)
        {
            _exitCode = await ScanRegionsAsync(session, options, cancellationToken).ConfigureAwait(false) ? 0 : 1;
            return;
        }

        if (options.Table)
        {
            _exitCode = await ReadTableAsync(session, options, cancellationToken).ConfigureAwait(false) ? 0 : 1;
            return;
        }

        if (options.Dump)
        {
            if (!await DumpRegionsAsync(session, options, cancellationToken).ConfigureAwait(false))
            {
                _exitCode = 1;
                return;
            }

            await RebootAsync(session, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        var table = await ReadPartitionTableAsync(session, options, cancellationToken).ConfigureAwait(false);
        if (table is null)
        {
            _exitCode = 1;
            return;
        }

        if (!await BackupAsync(session, options, table, cancellationToken).ConfigureAwait(false))
        {
            _exitCode = 1;
            return;
        }

        await RebootAsync(session, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> WaitForPortAsync(ProbeOptions options, HashSet<string> rejected,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Port))
        {
            Log($"pinned to {options.Port}; waiting for it to become openable. Attach the device AFTER this line.");
            var pinned = Stopwatch.StartNew();
            while (pinned.Elapsed < TimeSpan.FromSeconds(options.PortWaitSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await CanOpenAsync(options.Port, options, cancellationToken).ConfigureAwait(false))
                {
                    return options.Port;
                }

                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            Log($"pinned port {options.Port} never became openable.");
            return null;
        }

        var started = Stopwatch.StartNew();
        Log($"waiting up to {options.PortWaitSeconds}s for a MediaTek port. Attach the device AFTER this line.");
        string? lastReported = null;
        while (started.Elapsed < TimeSpan.FromSeconds(options.PortWaitSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (present.Length != 0)
            {
                var listing = string.Join(", ", present);
                if (!string.Equals(listing, lastReported, StringComparison.Ordinal))
                {
                    lastReported = listing;
                    Log($"ports present: {listing}");
                }

                // A rejection only covers the current attachment of that COM name. Once the port
                // disappears the reason it was rejected is gone too, so a real preloader that later
                // reuses the same name is still tried.
                rejected.IntersectWith(present);

                var candidates = present
                    .Where(name => !rejected.Contains(name))
                    .OrderBy(name => options.KnownPorts.Contains(name) ? 1 : 0)
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in candidates)
                {
                    if (await CanOpenAsync(candidate, options, cancellationToken).ConfigureAwait(false))
                    {
                        Log($"selected {candidate}{(options.KnownPorts.Contains(candidate) ? " (pre-existing, now openable)" : string.Empty)}.");
                        return candidate;
                    }
                }
            }

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        Log("no openable serial port appeared before the wait expired.");
        return null;
    }

    private static async Task<bool> CanOpenAsync(string port, ProbeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var transport = new SerialTransport(port, options.BaudRate, handshake: Handshake.None);
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<MtkSession> OpenWithRetryAsync(string port, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await MtkSession.OpenSerialAsync(port, options.BaudRate, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException error)
            {
                last = error;
                Log($"  open attempt {attempt} refused ({error.Message}); retrying.");
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"The port stayed unavailable after repeated attempts. {last?.Message}", last);
    }

    private static SerialConnectionOptions CreateSerial(string port, ProbeOptions options) => new()
    {
        PortName = port,
        BaudRate = options.BaudRate,
        Handshake = Handshake.None,
        TimeoutMs = options.TransferTimeoutMs
    };

    private static async Task<MtkDeviceInfo?> IdentifyAsync(string port, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        MtkSession? session = null;
        try
        {
            // The VCOM device enumerates a moment before its driver accepts an open call, and the whole
            // attach window lasts a few seconds, so the first refusal must not end the attempt.
            // VCOM 设备会先完成枚举，然后其驱动才接受打开调用，而整个接入窗口只有几秒，
            // 因此首次被拒不能终止这次尝试。
            session = await OpenWithRetryAsync(port, options, cancellationToken).ConfigureAwait(false);
            var device = session.DeviceInfo
                ?? throw new InvalidOperationException("The handshake completed without reporting a device identity.");
            Log($"handshake ok: hwcode=0x{device.HardwareCode:X4} subcode=0x{device.HardwareSubCode:X4} " +
                $"hwver=0x{device.HardwareVersion:X4} swver=0x{device.SoftwareVersion:X4}");
            Log($"  brom=0x{device.BromVersion?.ToString("X2") ?? "unknown"} " +
                $"bootloader=0x{device.BlVersion?.ToString("X2") ?? "unknown"} " +
                $"targetcfg=0x{device.TargetConfiguration:X8} secureboot={device.SecureBootEnabled} " +
                $"sla={device.SerialLinkAuthenticationRequired} daauth={device.DownloadAgentAuthenticationRequired} certrequired={device.RootCertificateRequired}");
            Log($"  meid={(device.Meid is { } m ? Convert.ToHexString(m) : "none")} " +
                $"socid={(device.Socid is { } s ? Convert.ToHexString(s) : "none")}");
            return device;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log($"handshake failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static Task<DaMatch?> ResolveLoaderAsync(MtkDeviceInfo device, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var loaders = options.Loader is { } explicitLoader ? [explicitLoader] : DaDiscovery.DefaultLoaderCandidates();
        Log($"resolving a loader for hwcode=0x{device.HardwareCode:X4}:");
        var match = DaDiscovery.Find(device, loaders, Log);
        if (match is null)
        {
            Log("FAILED: no candidate container holds an entry for this chip.");
            return Task.FromResult<DaMatch?>(null);
        }

        Log($"selected {match.Description}, stage1 region {match.StageOneRegion}, " +
            $"stage2 region {match.StageTwoRegion}.");
        var stageOne = match.Entry.Regions[match.StageOneRegion];
        var stageTwo = match.Entry.Regions[match.StageTwoRegion];
        Log($"  stage1: offset={stageOne.FileOffset} length={stageOne.Length} " +
            $"load=0x{stageOne.LoadAddress:X8} sig={stageOne.SignatureLength}");
        Log($"  stage2: offset={stageTwo.FileOffset} length={stageTwo.Length} " +
            $"load=0x{stageTwo.LoadAddress:X8} sig={stageTwo.SignatureLength}");
        return Task.FromResult<DaMatch?>(match);
    }

    private static async Task<MtkSession?> BootstrapAsync(string port, DaMatch match, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var bootstrap = options.BuildBootstrap(match);
        Log($"bootstrapping with protocol {options.Protocol}, storage {bootstrap.StorageKind}.");
        MtkSession? session = null;
        ICommTransport? transport = null;
        try
        {
            var serial = new SerialTransport(port, options.BaudRate, handshake: Handshake.None);
            await serial.ConnectAsync(cancellationToken).ConfigureAwait(false);
            transport = options.Trace ? new TracingTransport(serial, Log) : serial;
            session = await MtkBootstrap.InitializeAsync(transport, options.Protocol, bootstrap, cancellationToken)
                .ConfigureAwait(false);
            Log("download agent running and storage discovered.");
            return session;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log($"bootstrap failed: {error.GetType().Name}: {error.Message}");
            session?.Dispose();
            return null;
        }
    }

    private static async Task<bool> ReadTableAsync(MtkSession session, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Protocol != MtkDaProtocol.Legacy)
        {
            Log("FAILED: --table currently inspects Legacy regions only.");
            return false;
        }

        var snapshot = await session.ReadLegacyPartitionTableAsync((byte)options.GptPartitionId,
            cancellationToken).ConfigureAwait(false);
        Log($"region {snapshot.RegionName} ({snapshot.PartitionId}): detected {snapshot.Kind} table at offset " +
            $"{snapshot.TableOffset} (0x{snapshot.TableOffset:X}), layout header " +
            $"{(snapshot.LayoutHeader.IsRecognized ? snapshot.LayoutHeader.DescribeMediaType() : "absent")}, " +
            $"{snapshot.PartitionCount} partitions.");

        if (snapshot.Mbr is { } mbr)
        {
            Log($"  MBR: {mbr.TotalSectors} sectors, extended container {mbr.HasExtendedContainer}, " +
                $"{mbr.Logical.Count} logical, {mbr.ChainRecords.Count} chain records.");
            foreach (var entry in mbr.Primary)
            {
                Log($"    primary[{entry.Index}] populated={entry.IsPopulated} extended={entry.IsExtended} " +
                    $"type=0x{entry.PartitionType:X2} lba={entry.FirstLba} sectors={entry.SectorCount}");
            }

            foreach (var (first, count, type) in mbr.EnumerateExtents())
            {
                Log($"    extent type=0x{type:X2} lba={first} sectors={count} " +
                    $"bytes={first * (long)MtkMbrSnapshot.SectorSize}..{(first + (long)count) * MtkMbrSnapshot.SectorSize}");
            }
        }

        if (snapshot.Gpt is { } gpt)
        {
            Log($"  GPT: {gpt.Partitions.Count} partitions, block size {gpt.BlockSize}, disk {gpt.DiskId}.");
            foreach (var partition in gpt.Partitions)
            {
                Log($"    {partition.Name,-24} offset={partition.ByteOffset,14} length={partition.ByteLength,14}");
            }
        }

        Log("RESULT: partition table read.");
        return true;
    }

    private static async Task<GptTable?> ReadPartitionTableAsync(MtkSession session, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        GptTable table;
        if (options.Protocol == MtkDaProtocol.Legacy)
        {
            var snapshot = await session.ReadLegacyPartitionTableAsync((byte)options.GptPartitionId,
                cancellationToken).ConfigureAwait(false);
            Log($"region {snapshot.RegionName} ({snapshot.PartitionId}): detected {snapshot.Kind} table at " +
                $"offset {snapshot.TableOffset}, layout header {(snapshot.LayoutHeader.IsRecognized
                    ? snapshot.LayoutHeader.DescribeMediaType()
                    : "absent")}, {snapshot.PartitionCount} partitions.");
            if (snapshot.Gpt is not { } detected)
            {
                Log("FAILED: the selected region does not carry a GPT, which the backup path requires.");
                return null;
            }

            table = detected;
        }
        else
        {
            table = options.Protocol switch
            {
                MtkDaProtocol.XFlash => (await session.ReadXFlashGptAsync(options.GptPartitionId, cancellationToken)
                    .ConfigureAwait(false)).Table,
                _ => (await session.ReadXmlGptAsync(options.XmlRegionName, options.TransferTimeoutMs,
                    cancellationToken).ConfigureAwait(false)).Table
            };
        }

        Log($"partition table read: {table.Partitions.Count} partitions, block size {table.BlockSize}, " +
            $"disk {table.DiskId}, metadata {table.MetadataSha256[..16]}...");
        foreach (var partition in table.Partitions)
        {
            Log($"  {partition.Name,-24} offset={partition.ByteOffset,14} length={partition.ByteLength,14}");
        }

        return table;
    }

    private static async Task<bool> BackupAsync(MtkSession session, ProbeOptions options, GptTable table,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputPath);
        Log($"backing up every partition except [{string.Join(", ", options.ExcludedPartitions)}] " +
            $"into {options.OutputPath}.");

        var selected = table.Partitions
            .Where(partition => !options.ExcludedPartitions.Contains(partition.Name, StringComparer.OrdinalIgnoreCase))
            .Where(partition => options.LimitBytes is not { } limit || partition.ByteLength <= limit)
            .ToArray();

        if (selected.Length == 0)
        {
            Log("FAILED: no partition survived the exclusion filter, so nothing would be backed up.");
            return false;
        }

        Log($"backup covers {selected.Length} partitions, {selected.Sum(p => p.ByteLength)} bytes total.");

        foreach (var partition in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(options.OutputPath, $"{partition.Name}.img");
            var started = Stopwatch.StartNew();
            await using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await ReadPartitionAsync(session, options, partition, stream, cancellationToken).ConfigureAwait(false);
            }

            started.Stop();
            var written = new FileInfo(destination).Length;
            Log($"  {partition.Name,-24} -> {Path.GetFileName(destination),-32} {written,14} bytes " +
                $"{started.Elapsed.TotalSeconds,7:F1}s");
        }

        return true;
    }

    private static Task ReadPartitionAsync(MtkSession session, ProbeOptions options, GptPartition partition,
        Stream destination, CancellationToken cancellationToken) => options.Protocol switch
    {
        MtkDaProtocol.XFlash => session.ReadXFlashStorageAsync(options.GptPartitionId,
            partition.ByteOffset, partition.ByteLength, destination, null, cancellationToken),
        MtkDaProtocol.Legacy => session.ReadLegacyStorageAsync((byte)options.GptPartitionId,
            partition.ByteOffset, partition.ByteLength, destination, options.TransferTimeoutMs, null,
            cancellationToken),
        _ => session.ReadXmlStorageAsync(options.XmlRegionName, partition.ByteOffset, partition.ByteLength,
            destination, options.TransferTimeoutMs, null, cancellationToken)
    };

    private static async Task RebootAsync(MtkSession session, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        Log("requesting a normal-boot shutdown and ending the session.");
        switch (options.Protocol)
        {
            case MtkDaProtocol.XFlash:
                await session.ShutdownXFlashAsync(MtkXFlashShutdownMode.NormalBoot, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case MtkDaProtocol.Legacy:
                await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.NormalBoot, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                await session.RebootXmlAsync(new MtkXmlRebootOptions { Mode = MtkXmlBootMode.Normal },
                    options.TransferTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }

        Log("reboot acknowledged.");
    }

    private static async Task<bool> DumpRegionsAsync(MtkSession session, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var storage = session.LegacyStorage;
        if (storage is null)
        {
            Log("FAILED: the session did not publish a Legacy storage inventory.");
            return false;
        }

        Directory.CreateDirectory(options.OutputPath);
        Log($"dumping {storage.Kind} storage, CID {storage.CidHex}, {storage.Regions.Count} regions, " +
            $"into {options.OutputPath}.");

        const int chunkBytes = 1 << 20;
        var buffer = new byte[chunkBytes];
        var manifest = new List<string>
        {
            "cid " + storage.CidHex,
            "kind " + storage.Kind
        };

        var wroteAny = false;
        foreach (var region in storage.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = MtkLegacyEmmcRegion.Describe(region.PartitionId);
            if (region.CapacityBytes <= 0 || region.RequiresAuthenticatedAccess)
            {
                Log($"  region {region.PartitionId} ({name}): skipped, capacity {region.CapacityBytes} " +
                    "or authenticated access.");
                continue;
            }

            var limit = options.LimitBytes is { } cap && cap < region.CapacityBytes
                ? cap
                : region.CapacityBytes;
            var destination = Path.Combine(options.OutputPath, "region" + region.PartitionId + "-" + name + ".bin");
            Log($"  region {region.PartitionId} ({name}): dumping {limit} of {region.CapacityBytes} bytes " +
                $"-> {Path.GetFileName(destination)}");

            var started = Stopwatch.StartNew();
            long written = 0;
            var reported = 0L;
            await using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, chunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (long offset = 0; offset < limit;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(chunkBytes, limit - offset);
                    await session.ReadLegacyStorageAsync(region.PartitionId, offset, count, stream,
                        options.TransferTimeoutMs, null, cancellationToken).ConfigureAwait(false);
                    offset += count;
                    written += count;
                    if (written - reported >= 16L * chunkBytes)
                    {
                        reported = written;
                        var rate = written / Math.Max(started.Elapsed.TotalSeconds, 1e-6);
                        Log($"    {name}: {written} / {limit} bytes, {rate / 1024.0:F0} KiB/s");
                    }
                }
            }

            started.Stop();
            var actual = new FileInfo(destination).Length;
            Log($"  region {region.PartitionId} ({name}) done: {actual} bytes in " +
                $"{started.Elapsed.TotalSeconds:F0}s.");
            manifest.Add("region " + region.PartitionId + " " + name + " capacity=" + region.CapacityBytes +
                " dumped=" + actual);
            wroteAny |= actual > 0;
        }

        await File.WriteAllLinesAsync(Path.Combine(options.OutputPath, "manifest.txt"), manifest, cancellationToken)
            .ConfigureAwait(false);
        Log(wroteAny
            ? "RESULT: region dump complete."
            : "RESULT: every region produced an empty image.");
        return wroteAny;
    }

    private static async Task<bool> ScanRegionsAsync(MtkSession session, ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var storage = session.LegacyStorage;
        if (storage is null)
        {
            Log("FAILED: the session did not publish a Legacy storage inventory.");
            return false;
        }

        Log($"scanning {storage.Kind} storage, CID {storage.CidHex}, {storage.Regions.Count} regions.");
        var buffer = new byte[1024];
        var anyData = false;
        foreach (var region in storage.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.CapacityBytes <= 0 || region.RequiresAuthenticatedAccess)
            {
                Log($"  region {region.PartitionId} ({MtkLegacyEmmcRegion.Describe(region.PartitionId)}): " +
                    $"skipped, capacity {region.CapacityBytes} or authenticated access.");
                continue;
            }

            var selected = new long[] { 0, 0x1000, 0x100000 };
            foreach (var offset in selected)
            {
                if (offset + buffer.Length > region.CapacityBytes)
                {
                    continue;
                }

                using var sink = new MemoryStream();
                await session.ReadLegacyStorageAsync(region.PartitionId, offset, buffer.Length, sink,
                    options.TransferTimeoutMs, null, cancellationToken).ConfigureAwait(false);
                var data = sink.ToArray();
                var written = data.Length < buffer.Length ? data : data[..buffer.Length];
                var nonZero = written.Count(value => value != 0);
                var classified = Classify(written);
                if (nonZero > 0)
                {
                    anyData = true;
                }

                Log($"  region {region.PartitionId} ({MtkLegacyEmmcRegion.Describe(region.PartitionId)}) " +
                    $"offset 0x{offset:X}: {written.Length} bytes, {nonZero} non-zero, {classified}");
            }
        }

        Log(anyData
            ? "RESULT: at least one sampled region holds data."
            : "RESULT: every sampled region is entirely zero.");
        return anyData;
    }

    private static string Classify(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 8 && prefix[..8].SequenceEqual("EFI PART"u8))
        {
            return "GPT header";
        }

        if (prefix.Length >= 512 && prefix[510] == 0x55 && prefix[511] == 0xAA)
        {
            return "MBR signature";
        }

        if (prefix.Length >= 8 && prefix[..8].SequenceEqual("BRLYT"u8))
        {
            return "MTK BRLYT layout header";
        }

        return "no recognisable signature";
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {message}");
}