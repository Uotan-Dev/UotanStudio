using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Firmware.FileSystem.Ext4.Builder;

/// <summary>
/// EXT4 builder part for content-identical data blocks: it indexes blocks in disk-backed staging storage, allocates one physical block per distinct content across group boundaries, and re-reads every source block to compare it against the staged copy before writing.
/// <para>EXT4 构建器的组成部分，处理内容相同的数据块：用磁盘暂存存储为块建立索引，为每种不同内容只分配一个物理块并跨越组边界，
/// 写出前重新读取每个源块并与暂存副本逐字节比较。</para>
/// </summary>
public sealed partial class Ext4Builder
{
    private static BlockAllocationResult AllocateSharedDataBlocksMultiGroup(
        List<Node> nodes, List<GroupInfo> groups, int blocksPerGroup, int blockSize, int journalBlocks,
        SharedBlockPlan plan, CancellationToken cancellationToken)
    {
        var journalStart = -1L;
        var groupIndex = 0;
        var cursor = groups[0].FirstDataBlock;
        if (journalBlocks > 0)
        {
            if (journalBlocks > groups[0].BlocksInGroup - cursor)
            {
                throw new InvalidOperationException("EXT4 group zero cannot hold its metadata and internal journal.");
            }

            journalStart = cursor;
            cursor += journalBlocks;
            groups[0].UsedBlocks += journalBlocks;
        }

        bool EnsureSpace()
        {
            while (cursor >= (long)groupIndex * blocksPerGroup + groups[groupIndex].BlocksInGroup)
            {
                if (++groupIndex >= groups.Count)
                {
                    return false;
                }

                cursor = groups[groupIndex].FirstDataBlock;
            }

            return true;
        }

        bool AllocatePhysical(out long physical)
        {
            if (!EnsureSpace())
            {
                physical = -1;
                return false;
            }

            physical = cursor++;
            groups[groupIndex].UsedBlocks++;
            return true;
        }

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            node.Extents.Clear();
            if (node.ExternalXattrs is not null)
            {
                if (!EnsureSpace())
                {
                    return new BlockAllocationResult(false, journalStart, plan.HasDuplicates);
                }
                node.XattrBlock = cursor++;
                groups[groupIndex].UsedBlocks++;
            }
            if (IsBlockSharingCandidate(node))
            {
                var blockIndex = 0;
                foreach (var run in node.Runs)
                {
                    for (var logical = run.LogicalStart; logical < run.LogicalStart + run.Count; logical++)
                    {
                        var identity = checked((int)node.SharedBlockIds![blockIndex++]);
                        var physical = plan.PhysicalBlocks[identity];
                        if (physical < 0)
                        {
                            if (!AllocatePhysical(out physical))
                            {
                                return new BlockAllocationResult(false, journalStart, plan.HasDuplicates);
                            }

                            plan.PhysicalBlocks[identity] = physical;
                        }

                        AppendExtent(node.Extents, logical, physical);
                    }
                }
            }
            else
            {
                foreach (var run in node.Runs)
                {
                    var logical = run.LogicalStart;
                    var remaining = run.Count;
                    while (remaining > 0)
                    {
                        if (!EnsureSpace())
                        {
                            return new BlockAllocationResult(false, journalStart, plan.HasDuplicates);
                        }

                        var available = (long)groupIndex * blocksPerGroup + groups[groupIndex].BlocksInGroup - cursor;
                        var count = checked((ushort)Math.Min(Math.Min(remaining, available), MaxExtentLength));
                        node.Extents.Add((checked((uint)logical), checked((ulong)cursor), count));
                        groups[groupIndex].UsedBlocks += count;
                        cursor += count;
                        logical += count;
                        remaining -= count;
                    }
                }
            }

            var epb = EntriesPerBlock(blockSize);
            var leaves = node.Extents.Count > 4 ? (node.Extents.Count + epb - 1) / epb : 0;
            node.LeafBlocks = new long[leaves];
            node.IndexBlocks = leaves > 4 ? new long[(leaves + epb - 1) / epb] : [];
            for (var i = 0; i < node.LeafBlocks.Length; i++)
            {
                if (!AllocatePhysical(out node.LeafBlocks[i]))
                {
                    return new BlockAllocationResult(false, journalStart, plan.HasDuplicates);
                }
            }

            for (var i = 0; i < node.IndexBlocks.Length; i++)
            {
                if (!AllocatePhysical(out node.IndexBlocks[i]))
                {
                    return new BlockAllocationResult(false, journalStart, plan.HasDuplicates);
                }
            }

            var inodeGroup = checked((int)((node.Inode - 1) / (uint)groups[0].InodesInGroup));
            groups[inodeGroup].UsedInodes++;
            groups[inodeGroup].Nodes.Add(node);
            if (node.IsDirectory)
            {
                groups[inodeGroup].UsedDirectories++;
            }
        }

        return new BlockAllocationResult(true, journalStart, plan.HasDuplicates);
    }

    private static bool IsBlockSharingCandidate(Node node)
        => !node.IsDirectory && !node.IsSymlink && !node.IsSpecial && !node.IsInlineData && node.AttributeValue is null && node.Size > 0;

    private static void AppendExtent(List<(uint Logical, ulong Start, ushort Len)> extents, long logical, long physical)
    {
        if (extents.Count > 0)
        {
            var last = extents[^1];
            if ((long)last.Logical + last.Len == logical
                && (long)last.Start + last.Len == physical
                && last.Len < MaxExtentLength)
            {
                extents[^1] = (last.Logical, last.Start, checked((ushort)(last.Len + 1)));
                return;
            }
        }

        extents.Add((checked((uint)logical), checked((ulong)physical), 1));
    }

    private sealed class SharedBlockPlan : IDisposable
    {
        private readonly int _blockSize;
        private readonly CancellationToken _cancellationToken;
        private readonly FileStream _staging;

        private SharedBlockPlan(int blockSize, CancellationToken cancellationToken, FileStream staging,
            long uniqueBlockCount, bool hasDuplicates)
        {
            _blockSize = blockSize;
            _cancellationToken = cancellationToken;
            _staging = staging;
            UniqueBlockCount = uniqueBlockCount;
            HasDuplicates = hasDuplicates;
            PhysicalBlocks = Enumerable.Repeat(-1L, checked((int)uniqueBlockCount)).ToArray();
        }

        public long UniqueBlockCount { get; }
        public bool HasDuplicates { get; }
        public long[] PhysicalBlocks { get; }

        public static SharedBlockPlan Create(List<Node> nodes, int blockSize, Ext4Options options)
        {
            var candidateBlocks = nodes.Where(IsBlockSharingCandidate)
                .Sum(static node => node.Runs.Sum(static run => run.Count));
            if (candidateBlocks > int.MaxValue)
            {
                throw new NotSupportedException("EXT4 shared-block planning supports at most 2,147,483,647 data blocks.");
            }

            var stagingPath = Path.Combine(options.BlockIndexDirectory ?? Path.GetTempPath(),
                $"uotan-ext4-block-data-{Guid.NewGuid():N}.tmp");
            var staging = CreateStorage(stagingPath, 0);
            try
            {
                using var index = new DiskBackedDuplicateBlockIndex(candidateBlocks, blockSize, options, staging);
                var content = new byte[blockSize];
                foreach (var node in nodes)
                {
                    node.SharedBlockIds = null;
                    if (!IsBlockSharingCandidate(node))
                    {
                        continue;
                    }

                    node.SharedBlockIds = new List<long>(checked((int)node.DataBlockCount));
                    using var input = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        blockSize, FileOptions.SequentialScan);
                    if (input.Length != node.Size)
                    {
                        throw new IOException($"EXT4 source file size changed while planning shared blocks: '{node.RelativePath}'.");
                    }

                    foreach (var run in node.Runs)
                    {
                        for (var logical = run.LogicalStart; logical < run.LogicalStart + run.Count; logical++)
                        {
                            options.CancellationToken.ThrowIfCancellationRequested();
                            var sourceOffset = checked(logical * blockSize);
                            var length = checked((int)Math.Min(blockSize, node.Size - sourceOffset));
                            input.Position = sourceOffset;
                            input.ReadExactly(content.AsSpan(0, length));
                            node.SharedBlockIds.Add(index.Resolve(content.AsMemory(0, length)));
                        }
                    }
                }

                staging.SetLength(checked(index.UniqueBlockCount * blockSize));
                return new SharedBlockPlan(blockSize, options.CancellationToken, staging,
                    index.UniqueBlockCount, index.HasDuplicates);
            }
            catch
            {
                staging.Dispose();
                throw;
            }
        }

        public void ValidateSources(List<Node> nodes)
        {
            var actual = new byte[_blockSize];
            var expected = new byte[_blockSize];
            foreach (var node in nodes.Where(IsBlockSharingCandidate))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                using var input = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    _blockSize, FileOptions.SequentialScan);
                if (input.Length != node.Size)
                {
                    throw new IOException($"EXT4 source file size changed while building shared blocks: '{node.RelativePath}'.");
                }

                var blockIndex = 0;
                var totalBlocks = (node.Size + _blockSize - 1) / _blockSize;
                for (var logical = 0L; logical < totalBlocks; logical++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var sourceOffset = checked(logical * _blockSize);
                    var length = checked((int)Math.Min(_blockSize, node.Size - sourceOffset));
                    input.Position = sourceOffset;
                    input.ReadExactly(actual.AsSpan(0, length));
                    if (node.HoleBlocks?.Contains(checked((int)logical)) == true)
                    {
                        if (actual.AsSpan(0, length).ContainsAnyExcept((byte)0))
                        {
                            throw new IOException($"EXT4 source file changed while building shared blocks: '{node.RelativePath}'.");
                        }

                        continue;
                    }

                    var identity = node.SharedBlockIds![blockIndex++];
                    ReadExactlyAt(_staging.SafeFileHandle, expected.AsSpan(0, length),
                        checked(identity * _blockSize));
                    if (!actual.AsSpan(0, length).SequenceEqual(expected.AsSpan(0, length)))
                    {
                        throw new IOException($"EXT4 source file changed while building shared blocks: '{node.RelativePath}'.");
                    }
                }
            }
        }

        public void WriteUniqueBlocks(Stream output, long baseOffset)
        {
            var content = new byte[_blockSize];
            for (var identity = 0; identity < PhysicalBlocks.Length; identity++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReadExactlyAt(_staging.SafeFileHandle, content, checked((long)identity * _blockSize));
                WriteAt(output, baseOffset, checked(PhysicalBlocks[identity] * _blockSize), content, _blockSize);
            }
        }

        public void Dispose() => _staging.Dispose();
    }

    private sealed class DiskBackedDuplicateBlockIndex : IDisposable
    {
        private const int RecordSize = 64;
        private const int HashOffset = 1;
        private const int LengthOffset = 36;
        private const int IdentityOffset = 40;

        private readonly int _blockSize;
        private readonly CancellationToken _cancellationToken;
        private readonly Func<ReadOnlyMemory<byte>, byte[]>? _hashProvider;
        private readonly FileStream _storage;
        private readonly SafeFileHandle _handle;
        private readonly SafeFileHandle _stagingHandle;
        private readonly byte[] _record = new byte[RecordSize];
        private readonly byte[] _comparison;
        private readonly long _slotMask;

        public DiskBackedDuplicateBlockIndex(long blockCount, int blockSize, Ext4Options options, FileStream staging)
        {
            _blockSize = blockSize;
            _cancellationToken = options.CancellationToken;
            _hashProvider = options.BlockHashProvider;
            _comparison = new byte[blockSize];
            _stagingHandle = staging.SafeFileHandle;

            var requiredSlots = checked(Math.Max(16L, blockCount * 2));
            var slotCount = 16L;
            while (slotCount < requiredSlots)
            {
                slotCount = checked(slotCount * 2);
            }

            _slotMask = slotCount - 1;
            var path = Path.Combine(options.BlockIndexDirectory ?? Path.GetTempPath(),
                $"uotan-ext4-block-index-{Guid.NewGuid():N}.tmp");
            _storage = CreateStorage(path, checked(slotCount * RecordSize));
            _handle = _storage.SafeFileHandle;
        }

        public long UniqueBlockCount { get; private set; }
        public bool HasDuplicates { get; private set; }

        public long Resolve(ReadOnlyMemory<byte> content)
        {
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            if (_hashProvider is null)
            {
                SHA256.HashData(content.Span, hash);
            }
            else
            {
                var supplied = _hashProvider(content);
                if (supplied.Length != SHA256.HashSizeInBytes)
                {
                    throw new InvalidOperationException("EXT4 block hash provider must return exactly 32 bytes.");
                }

                supplied.CopyTo(hash);
            }

            var slot = (long)(BinaryPrimitives.ReadUInt64LittleEndian(hash) & (ulong)_slotMask);
            for (var probe = 0L; probe <= _slotMask; probe++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var position = checked(slot * RecordSize);
                _record.AsSpan().Clear();
                ReadExactlyAt(_handle, _record, position);
                if (_record[0] == 0)
                {
                    var identity = UniqueBlockCount++;
                    RandomAccess.Write(_stagingHandle, content.Span, checked(identity * _blockSize));
                    _record[0] = 1;
                    hash.CopyTo(_record.AsSpan(HashOffset, SHA256.HashSizeInBytes));
                    BinaryPrimitives.WriteInt32LittleEndian(_record.AsSpan(LengthOffset, 4), content.Length);
                    BinaryPrimitives.WriteInt64LittleEndian(_record.AsSpan(IdentityOffset, 8), identity);
                    RandomAccess.Write(_handle, _record, position);
                    return identity;
                }

                if (_record.AsSpan(HashOffset, SHA256.HashSizeInBytes).SequenceEqual(hash)
                    && BinaryPrimitives.ReadInt32LittleEndian(_record.AsSpan(LengthOffset, 4)) == content.Length)
                {
                    var identity = BinaryPrimitives.ReadInt64LittleEndian(_record.AsSpan(IdentityOffset, 8));
                    ReadExactlyAt(_stagingHandle, _comparison.AsSpan(0, content.Length),
                        checked(identity * _blockSize));
                    if (content.Span.SequenceEqual(_comparison.AsSpan(0, content.Length)))
                    {
                        HasDuplicates = true;
                        return identity;
                    }
                }

                slot = (slot + 1) & _slotMask;
            }

            throw new InvalidOperationException("EXT4 duplicate block index is full.");
        }

        public void Dispose() => _storage.Dispose();
    }

    private static FileStream CreateStorage(string path, long length)
    {
        var storage = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        try
        {
            storage.SetLength(length);
            return storage;
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    private static void ReadExactlyAt(SafeFileHandle handle, Span<byte> destination, long position)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = RandomAccess.Read(handle, destination[read..], checked(position + read));
            if (count == 0)
            {
                throw new EndOfStreamException("EXT4 shared-block staging data was truncated.");
            }

            read += count;
        }
    }
}