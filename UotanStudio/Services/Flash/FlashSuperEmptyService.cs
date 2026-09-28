using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Validated standalone LP metadata and its required physical Super capacity.</summary>
internal sealed record PreparedSuperEmpty(byte[] MetadataImage, long DeviceSize);

/// <summary>Applies a validated standalone super_empty metadata image in Fastbootd.</summary>
public static class FlashSuperEmptyService
{
    /// <summary>Validates the complete standalone layout before opening any device session.</summary>
    internal static PreparedSuperEmpty Prepare(string imagePath)
    {
        using var input = File.OpenRead(imagePath);
        LpMetadata metadata;
        try { metadata = MetadataReader.ReadFromImageStream(input); }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The standalone empty Super metadata is truncated.", ex);
        }
        if (metadata.BlockDevices.Count != 1 || metadata.Extents.Count != 0 ||
            metadata.Partitions.Any(partition => partition.NumExtents != 0) ||
            Encoding.ASCII.GetString(metadata.BlockDevices[0].PartitionName).TrimEnd('\0') != "super" ||
            metadata.BlockDevices[0].Size is 0 or > long.MaxValue)
            throw new InvalidDataException("The file is not a standalone empty Super metadata image.");

        var geometry = MetadataWriter.SerializeGeometry(metadata.Geometry);
        var table = MetadataWriter.SerializeMetadata(metadata);
        if (input.Length != geometry.LongLength + table.LongLength)
            throw new InvalidDataException("The Super image contains data or is not a standalone empty image.");
        return new PreparedSuperEmpty([.. geometry, .. table], (long)metadata.BlockDevices[0].Size);
    }

    /// <summary>Checks the device mode and capacity before sending the validated metadata.</summary>
    internal static void Apply(FastbootSession session, PreparedSuperEmpty prepared, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.GetVariable("is-userspace", useCache: false) != "yes")
            throw new InvalidOperationException("Applying super_empty requires Fastbootd.");
        var deviceSize = session.GetPartitionSizeLong("super");
        if (deviceSize <= 0 || deviceSize != prepared.DeviceSize)
            throw new InvalidDataException("The device Super size does not match super_empty metadata.");
        ct.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(prepared.MetadataImage, writable: false);
        session.UpdateSuper("super", stream, wipe: true, cancellationToken: ct).ThrowIfFailed();
    }

    /// <summary>Opens only the selected Fastboot target after file validation.</summary>
    internal static Task WipeAsync(DetectedDevice device, PreparedSuperEmpty prepared, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            App.DeviceMonitor.RunFastboot(device, session =>
            {
                if (session.GetVariable("is-userspace", useCache: false) == "yes")
                    Apply(session, prepared, ct);
                else
                    ApplyBootloader(session, prepared, ct);
            }, ct);
        }, ct);
    }

    /// <summary>Flashes the canonical physical empty Super image without expanding its data to disk.</summary>
    internal static void ApplyBootloader(FastbootSession session, PreparedSuperEmpty prepared, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.GetVariable("is-userspace", useCache: false) == "yes")
            throw new InvalidOperationException("Physical Super flashing requires bootloader Fastboot.");
        if (session.GetPartitionSizeLong("super") != prepared.DeviceSize)
            throw new InvalidDataException("The device Super size does not match super_empty metadata.");

        var directory = Directory.CreateTempSubdirectory("uotan-super-empty-").FullName;
        try
        {
            var image = Path.Combine(directory, "physical-super.sparse");
            FlashSuperEmptySparse.Build(prepared, image, ct);
            var maxDownload = session.GetMaxDownloadSize();
            IReadOnlyList<string> chunks;
            if (new FileInfo(image).Length <= maxDownload)
            {
                chunks = [image];
            }
            else
            {
                SparseImageConverter.ResparseImage(image, Path.Combine(directory, "chunk_{0}.sparse"), maxDownload);
                chunks = Directory.GetFiles(directory, "chunk_*.sparse")
                    .OrderBy(path => int.Parse(Path.GetFileNameWithoutExtension(path)["chunk_".Length..],
                        System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (chunks.Count == 0) throw new InvalidDataException("Super image resparse produced no chunks.");
            }

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                using var input = File.OpenRead(chunk);
                session.FlashPartition("super", input, input.Length,
                    cancellationToken: ct, resolveSlot: false).ThrowIfFailed();
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}