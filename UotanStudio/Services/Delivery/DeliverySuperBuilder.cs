using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Services.Delivery;

/// <summary>Builds actual LP partition contents using absolute sector addresses and bounded streams.</summary>
internal static class DeliverySuperBuilder
{
    internal static List<DeliveryFile> ExpandPartitions(IReadOnlyList<DeliveryFile> files, DeliverySuperOptions options)
    {
        var result = files.Select(f => options.AutoZero && !f.Partition.EndsWith("_a", StringComparison.Ordinal)
            && !f.Partition.EndsWith("_b", StringComparison.Ordinal) ? f with { Partition = f.Partition + "_a" } : f).ToList();
        if (options.AutoZero)
            foreach (var file in result.ToArray())
                if (file.Partition.EndsWith("_a", StringComparison.Ordinal))
                {
                    var name = file.Partition[..^2] + "_b";
                    if (result.All(f => f.Partition != name)) result.Add(new DeliveryFile { Partition = name });
                }
        if (result.Any(f => !DeliveryValidation.IsPartition(f.Partition)) || result.Select(f => f.Partition).Distinct().Count() != result.Count)
            throw new InvalidDataException("Super 分区名无效或重复 / Invalid or duplicate Super partition.");
        return result;
    }

    internal static Task BuildAsync(IReadOnlyList<DeliveryFile> files, DeliverySuperOptions options, string output,
        IProgress<double>? progress = null, CancellationToken token = default) => Task.Run(() =>
    {
        if (options.DeviceSize > long.MaxValue) throw new InvalidDataException("Super size exceeds stream limits.");
        var partitions = ExpandPartitions(files, options);
        var builder = MetadataBuilder.New(options.DeviceSize, options.MetadataSize, options.Slots, options.BlockSize);
        var groups = DeliveryGroups.Parse(DeliveryGroups.Format(options.Groups));
        if (groups.Count == 0) groups.Add(new() { Name = options.Group, Partitions = partitions.Select(p => p.Partition).ToList() });
        foreach (var group in groups) builder.AddGroup(group.Name, group.Size);
        var lengths = new List<long>();
        foreach (var file in partitions)
        {
            token.ThrowIfCancellationRequested();
            long length = 0;
            if (file.SourcePath.Length > 0 && !options.Empty)
            {
                DeliveryValidation.CheckSource(file);
                using var source = SparseReader.ImportAuto(file.SourcePath);
                length = checked((long)source.Header.TotalBlocks * source.Header.BlockSize);
            }
            lengths.Add(length);
            var group = groups.SingleOrDefault(g => g.Partitions.Contains(file.Partition))
                ?? throw new InvalidDataException("请在分组配置中指定分区归属：" + file.Partition);
            builder.AddPartition(file.Partition, group.Name, (ulong)length, 1);
        }
        var metadata = builder.Build();
        metadata.Header = metadata.Header with { Flags = options.VirtualAb ? 1u : 0u, MinorVersion = options.VirtualAb ? (ushort)2 : (ushort)0 };
        metadata.BlockDevices[0] = metadata.BlockDevices[0] with { PartitionName = Encoding.UTF8.GetBytes(options.DeviceName) };
        _ = MetadataWriter.SerializeMetadata(metadata);
        var raw = output + ".raw-" + Guid.NewGuid().ToString("N");
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (options.Empty)
            {
                // liblp standalone empty-image format: geometry at zero, one metadata blob.
                using var empty = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                empty.Write(MetadataWriter.SerializeGeometry(metadata.Geometry));
                empty.Write(MetadataWriter.SerializeMetadata(metadata));
            }
            else
            {
                using (var stream = new FileStream(raw, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.SetLength((long)options.DeviceSize);
                    MetadataWriter.WriteToImageStream(stream, metadata);
                    long done = 0;
                    var total = lengths.Sum();
                    for (var i = 0; i < partitions.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (lengths[i] == 0) continue;
                        var part = metadata.Partitions[i];
                        var extent = metadata.Extents[(int)part.FirstExtentIndex];
                        stream.Position = checked((long)extent.TargetData * 512);
                        using var source = SparseReader.ImportAuto(partitions[i].SourcePath);
                        using var bounded = new CountingWriteStream(stream, lengths[i], true, token);
                        using var tracked = new DeliveryWriteProgressStream(bounded, value => progress?.Report((done + value) * 75.0 / Math.Max(1, total)), token);
                        source.WriteRawToStream(tracked);
                        if (bounded.BytesWritten != lengths[i]) throw new InvalidDataException("Partition size changed during build.");
                        done += lengths[i];
                    }
                }
                if (options.Sparse)
                {
                    using var input = File.OpenRead(raw);
                    using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                    using var tracked = new DeliveryWriteProgressStream(destination,
                        value => progress?.Report(75 + Math.Min(24, value * 24.0 / (double)options.DeviceSize)), token);
                    SparseImageConverter.ConvertRawToSparse(input, tracked, options.BlockSize, input.Length);
                }
                else File.Move(raw, temporary);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, false);
            progress?.Report(100);
        }
        finally { File.Delete(raw); File.Delete(temporary); }
    }, token);
}

/// <summary>Non-owning write adapter providing bounded-frequency progress and cancellation for synchronous codecs.</summary>
internal sealed class DeliveryWriteProgressStream(Stream inner, Action<long> progress, CancellationToken token) : Stream
{
    private long _written;
    private long _last;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        inner.Write(buffer);
        _written = checked(_written + buffer.Length);
        if (Environment.TickCount64 - _last >= 100) { progress(_written); _last = Environment.TickCount64; }
    }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
