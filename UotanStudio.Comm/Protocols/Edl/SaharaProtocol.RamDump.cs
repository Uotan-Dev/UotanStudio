using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Describes one Sahara ramdump region parsed from the 64-bit memory debug table.
/// <para>描述从 64 位内存调试表中解析出的一个 Sahara ramdump 区域。</para>
/// </summary>
public sealed record SaharaRamDumpRegion(
    ulong Type,
    ulong Address,
    ulong Length,
    string TypeName,
    string FileName)
{
    /// <summary>
    /// Fixed record size of one 64-bit debug table entry.
    /// <para>64 位调试表单个条目的定长记录大小。</para>
    /// </summary>
    public const int RecordBytes = 64;

    /// <summary>
    /// Fixed ASCII string width inside one table record.
    /// <para>表记录内定长 ASCII 字符串的宽度。</para>
    /// </summary>
    public const int StringBytes = 20;
}

/// <summary>
/// Progress snapshot emitted while one ramdump region is being downloaded.
/// <para>下载一个 ramdump 区域期间发出的进度快照。</para>
/// </summary>
/// <param name="Region">The region being downloaded.<para>正在下载的区域。</para></param>
/// <param name="CompletedBytes">Bytes persisted for the region so far.<para>该区域已落盘的字节数。</para></param>
/// <param name="TotalBytes">Total region size in bytes.<para>区域总字节数。</para></param>
public sealed record SaharaRamDumpProgress(SaharaRamDumpRegion Region, ulong CompletedBytes, ulong TotalBytes);

public sealed partial class SaharaProtocol
{
    private const int RamDumpControlTimeoutMs = 1000;
    private const int RamDumpDataTimeoutMs = 30000;
    private const int RamDumpBlockBytes = 1024 * 1024;
    private const int RamDumpFileBufferBytes = 1024 * 1024;
    private const int RamDumpMaxTableBytes = 64 * 1024;
    private const int RamDumpHelloPacketBytes = 0x30;
    private const int RamDumpMemoryDebug64Bytes = 0x18;
    private const int RamDumpSimpleCommandBytes = 0x08;
    private const int RamDumpMaxRegions = 1024;

    /// <summary>
    /// Collects a Sahara ramdump from a device already in memory-debug mode, writing each region to
    /// <paramref name="outputDirectory"/> and issuing a Sahara reset on success.
    /// <para>从已处于内存调试模式的设备收集 Sahara ramdump,把每个区域写入
    /// <paramref name="outputDirectory"/>,成功后发出 Sahara 复位。</para>
    /// </summary>
    /// <param name="outputDirectory">
    /// Existing or creatable directory that receives one file per region.
    /// <para>接收每个区域一个文件的现有或可创建目录。</para>
    /// </param>
    /// <param name="segmentFilter">
    /// Optional comma-separated case-insensitive substrings matched against file names; <see langword="null"/>
    /// keeps every region. <para>可选的逗号分隔、按文件名不区分大小写匹配的子串;<see langword="null"/> 保留全部区域。</para>
    /// </param>
    /// <param name="progress">Optional per-region progress callback.<para>可选的区域级进度回调。</para></param>
    /// <param name="cancellationToken">A cancellation token.<para>取消令牌。</para></param>
    /// <returns>The regions that were downloaded, in table order.
    /// <para>已下载的区域,按表顺序。</para></returns>
    /// <exception cref="SaharaException">
    /// The device answered with a non-memory-debug handshake, a malformed packet, or a failed reset.
    /// <para>设备以非内存调试模式握手、包格式非法或复位失败。</para>
    /// </exception>
    public async Task<IReadOnlyList<SaharaRamDumpRegion>> CollectRamDumpAsync(
        string outputDirectory,
        string? segmentFilter = null,
        IProgress<SaharaRamDumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        var resetSent = false;
        try
        {
            var (header, body) = await ReceiveSaharaPacketAsync(RamDumpControlTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (header.Command == SaharaCommand.Hello)
            {
                await AcknowledgeMemoryDebugHelloAsync(header, body, cancellationToken).ConfigureAwait(false);
                (header, body) = await ReceiveSaharaPacketAsync(RamDumpControlTimeoutMs, cancellationToken).ConfigureAwait(false);
            }

            if (header.Command == SaharaCommand.MemoryDebug)
            {
                throw new SaharaException(
                    "The device offered 32-bit Sahara MEMORY_DEBUG; only the 64-bit MEMORY_DEBUG64 flow is supported.");
            }

            if (header.Command != SaharaCommand.MemoryDebug64)
            {
                throw new SaharaException($"Expected Sahara MEMORY_DEBUG64, received {header.Command}.");
            }

            var table = await ParseMemoryDebugTableAsync(header, body, cancellationToken).ConfigureAwait(false);
            var selected = table.Where(r => MatchesRamDumpFilter(r.FileName, segmentFilter)).ToArray();
            ValidateRamDumpOutputPaths(outputRoot, selected);
            foreach (var region in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DownloadRamDumpRegionAsync(region, outputRoot, progress, cancellationToken).ConfigureAwait(false);
            }

            await SendSaharaResetAsync(cancellationToken).ConfigureAwait(false);
            resetSent = true;
            return selected;
        }
        catch
        {
            if (!resetSent)
            {
                await TryResetAfterRamDumpFailureAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task AcknowledgeMemoryDebugHelloAsync(
        SaharaPacketHeader hello, byte[] body, CancellationToken cancellationToken)
    {
        if (hello.Length != RamDumpHelloPacketBytes)
        {
            throw new SaharaException($"Unexpected Sahara HELLO packet length {hello.Length}; expected {RamDumpHelloPacketBytes}.");
        }

        var modeOffset = 0x14 - sizeof(uint) * 2;
        if (body.Length < modeOffset + sizeof(uint))
        {
            throw new SaharaException($"The Sahara HELLO body is too short ({body.Length} bytes) to carry its mode.");
        }

        var mode = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(modeOffset));
        if (mode != (uint)SaharaMode.MemoryDebug)
        {
            throw new SaharaException(
                $"Expected a Sahara MemoryDebug HELLO, but the device reported mode 0x{mode:X}.");
        }

        await WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = EdlConstants.SaharaVersion,
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Status = 0,
            Mode = SaharaMode.MemoryDebug
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(SaharaPacketHeader Header, byte[] Body)> ReceiveSaharaPacketAsync(
        int timeoutMs, CancellationToken cancellationToken)
    {
        var headerBytes = await ReadExactAsync(sizeof(uint) * 2, timeoutMs, cancellationToken).ConfigureAwait(false);
        var header = BytesToStruct<SaharaPacketHeader>(headerBytes);
        if (header.Length < sizeof(uint) * 2 || header.Length > EdlConstants.DefaultMaxPacketLength)
        {
            throw new SaharaException($"Sahara packet length {header.Length} is outside the accepted range.");
        }

        var remaining = (int)header.Length - sizeof(uint) * 2;
        var body = remaining > 0
            ? await ReadExactAsync(remaining, timeoutMs, cancellationToken).ConfigureAwait(false)
            : [];
        if (body.Length != remaining)
        {
            throw new SaharaException("The Sahara packet body was truncated.");
        }

        return (header, body);
    }

    private async Task<SaharaRamDumpRegion[]> ParseMemoryDebugTableAsync(
        SaharaPacketHeader packet, byte[] body, CancellationToken cancellationToken)
    {
        if (packet.Length != RamDumpMemoryDebug64Bytes)
        {
            throw new SaharaException(
                $"Unexpected Sahara MEMORY_DEBUG64 packet length {packet.Length}; expected {RamDumpMemoryDebug64Bytes}.");
        }

        var tableAddress = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(0x00, 8));
        var tableLength = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(0x08, 8));
        if (tableLength > RamDumpMaxTableBytes)
        {
            throw new SaharaException($"Sahara debug table length {tableLength} exceeds the {RamDumpMaxTableBytes}-byte limit.");
        }

        if (tableLength % SaharaRamDumpRegion.RecordBytes != 0)
        {
            throw new SaharaException(
                $"Sahara debug table length {tableLength} is not a multiple of the {SaharaRamDumpRegion.RecordBytes}-byte record size.");
        }

        ValidateRamDumpRange(tableAddress, tableLength, "Sahara debug table");
        var tableBuffer = new byte[tableLength];
        if (tableBuffer.Length > 0)
        {
            await ReadSaharaMemoryAsync(tableAddress, tableBuffer, cancellationToken).ConfigureAwait(false);
        }

        var regions = new List<SaharaRamDumpRegion>(tableBuffer.Length / SaharaRamDumpRegion.RecordBytes);
        for (var offset = 0; offset < tableBuffer.Length; offset += SaharaRamDumpRegion.RecordBytes)
        {
            var record = tableBuffer.AsSpan(offset, SaharaRamDumpRegion.RecordBytes);
            regions.Add(new SaharaRamDumpRegion(
                BinaryPrimitives.ReadUInt64LittleEndian(record),
                BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]),
                ReadSaharaFixedAscii(record[0x18..]),
                ReadSaharaFixedAscii(record[0x2C..])));
            if (regions.Count > RamDumpMaxRegions)
            {
                throw new SaharaException($"The Sahara debug table lists more than {RamDumpMaxRegions} regions.");
            }
        }

        return [.. regions];
    }

    private async Task ReadSaharaMemoryAsync(ulong address, byte[] destination, CancellationToken cancellationToken)
    {
        var received = 0;
        var deadline = Environment.TickCount64 + RamDumpDataTimeoutMs;
        await WriteDataAsync(BuildSaharaMemoryRead64(address, (ulong)destination.Length), cancellationToken).ConfigureAwait(false);
        while (received < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingMs = (int)Math.Min(RamDumpDataTimeoutMs, deadline - Environment.TickCount64);
            if (remainingMs <= 0)
            {
                throw new SaharaException(
                    $"The Sahara memory read stalled at {received}/{destination.Length} bytes.");
            }

            int n;
            try
            {
                n = await _transport.ReadAsync(destination, received, destination.Length - received, Math.Max(1, remainingMs), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new SaharaException($"The Sahara memory read stalled at {received}/{destination.Length} bytes.");
            }

            if (n <= 0)
            {
                throw new SaharaException("The device returned no data for the Sahara memory read.");
            }

            received += n;
        }
    }

    private async Task DownloadRamDumpRegionAsync(
        SaharaRamDumpRegion region,
        string outputRoot,
        IProgress<SaharaRamDumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(outputRoot, region.FileName);
        var partialPath = targetPath + ".partial";
        var completed = 0UL;
        try
        {
            await using (var output = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None, RamDumpFileBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (completed < region.Length)
                {
                    var chunk = (int)Math.Min(RamDumpBlockBytes, region.Length - completed);
                    var address = checked(region.Address + completed);
                    var buffer = new byte[chunk];
                    await WriteDataAsync(BuildSaharaMemoryRead64(address, (ulong)chunk), cancellationToken).ConfigureAwait(false);
                    var received = 0;
                    var dataDeadline = Environment.TickCount64 + RamDumpDataTimeoutMs;
                    while (received < chunk)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var remainingMs = (int)Math.Min(RamDumpDataTimeoutMs, dataDeadline - Environment.TickCount64);
                        if (remainingMs <= 0)
                        {
                            throw new SaharaException(
                                $"The device stopped answering the ramdump read of '{region.FileName}' at {received}/{chunk} bytes.");
                        }

                        int n;
                        try
                        {
                            n = await _transport.ReadAsync(buffer, received, chunk - received, Math.Max(1, remainingMs), cancellationToken).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            throw new SaharaException(
                                $"The device stopped answering the ramdump read of '{region.FileName}' at {received}/{chunk} bytes.");
                        }

                        if (n <= 0)
                        {
                            throw new SaharaException(
                                $"The device returned no data for ramdump region '{region.FileName}'.");
                        }

                        received += n;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, received), cancellationToken).ConfigureAwait(false);
                    completed += (ulong)received;
                    progress?.Report(new SaharaRamDumpProgress(region, completed, region.Length));
                }
            }

            File.Move(partialPath, targetPath, true);
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }
    }

    private async Task SendSaharaResetAsync(CancellationToken cancellationToken)
    {
        var reset = BuildSaharaSimpleCommand(SaharaCommand.Reset);
        await WriteDataAsync(reset, cancellationToken).ConfigureAwait(false);
        var (response, _) = await ReceiveSaharaPacketAsync(RamDumpControlTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (response.Command != SaharaCommand.ResetResponse || response.Length != RamDumpSimpleCommandBytes)
        {
            throw new SaharaException($"Expected Sahara RESET_RESP, received {response.Command} (len {response.Length}).");
        }
    }

    private async Task TryResetAfterRamDumpFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reset = BuildSaharaSimpleCommand(SaharaCommand.Reset);
            await WriteDataAsync(reset, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Best-effort Sahara reset after ramdump failure failed: {ex.Message}");
        }
    }

    private byte[] BuildSaharaMemoryRead64(ulong address, ulong length)
    {
        var packet = new byte[RamDumpMemoryDebug64Bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)SaharaCommand.MemoryRead64);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), address);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(0x10), length);
        return packet;
    }

    private static byte[] BuildSaharaSimpleCommand(SaharaCommand command)
    {
        var packet = new byte[RamDumpSimpleCommandBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        return packet;
    }

    private static void ValidateRamDumpRange(ulong address, ulong length, string description)
    {
        if (length > ulong.MaxValue - address)
        {
            throw new SaharaException($"The {description} address range overflows the 64-bit address space.");
        }
    }

    private static string ReadSaharaFixedAscii(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(terminator >= 0 ? value[..terminator] : value);
        return text.TrimEnd('\u0000');
    }

    private static bool MatchesRamDumpFilter(string fileName, string? filter)
    {
        if (filter is null)
        {
            return true;
        }

        foreach (var pattern in filter.Split(','))
        {
            if (fileName.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateRamDumpOutputPaths(string outputRoot, IReadOnlyList<SaharaRamDumpRegion> regions)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in regions)
        {
            if (region.FileName.Contains('\\') || region.FileName.Contains('/') || region.FileName.Contains(".."))
            {
                throw new SaharaException($"The ramdump region file name '{region.FileName}' is not a plain file name.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(outputRoot, region.FileName));
            EnsureRamDumpPathInside(outputRoot, targetPath);
            var partialPath = targetPath + ".partial";
            EnsureRamDumpPathInside(outputRoot, partialPath);
            if (!used.Add(targetPath) || !used.Add(partialPath))
            {
                throw new SaharaException($"The ramdump region '{region.FileName}' conflicts with another output file name.");
            }
        }
    }

    private static void EnsureRamDumpPathInside(string outputRoot, string path)
    {
        var relative = Path.GetRelativePath(outputRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new SaharaException($"The ramdump output path escapes the output directory: '{path}'.");
        }
    }
}