using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Diff.Zucchini;

/// <summary>
/// Bounded, write-free preflight that rejects Zucchini containers declaring an executable type other than NoOp
/// before any destination byte is produced.
/// <para>有界且不产生写入的预扫描，在生成任何目标字节之前拒绝声明了 NoOp 之外可执行类型的 Zucchini 容器。</para>
/// </summary>
/// <remarks>
/// The executable type lives inside the Brotli-compressed Zucchini blob and never in the protobuf manifest, so the
/// operation-type classification a payload extractor can do up front cannot see it. This scanner therefore seeks to
/// each <see cref="InstallOperation.Types.Type.Zucchini"/> blob, decodes it through
/// <see cref="StrictBrotliReadStream"/>, and walks the header plus the element-header table using exactly the layout
/// <see cref="ZucchiniPatchApplier.Apply(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte}, System.Span{byte})"/>
/// reads. It touches only the blob: it never opens the base image, never writes a destination extent, and keeps one
/// reusable fixed-size buffer instead of materializing the decoded container. Decoding is capped at
/// <see cref="MaximumDecodedBlobBytes"/>, the same ceiling the applying path enforces, and each element is charged at
/// least <see cref="MinimumElementBytes"/> against that budget, so an untrusted blob can drive neither unbounded work
/// nor unbounded allocation.
/// <para>可执行类型位于 Brotli 压缩的 Zucchini 数据块内部，从不出现在 protobuf 清单里，因此提取器在事前能做的操作
/// 类型分类看不到它。本扫描器于是定位到每个 <see cref="InstallOperation.Types.Type.Zucchini"/> 数据块，
/// 经 <see cref="StrictBrotliReadStream"/> 解码，并按
/// <see cref="ZucchiniPatchApplier.Apply(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte}, System.Span{byte})"/>
/// 读取的完全相同布局走一遍头部与元素头表。它只触碰数据块：从不打开基座镜像、从不写入目标 extent，
/// 并保留一个可复用的固定大小缓冲区，而不是把解码后的容器整体物化。解码上限为
/// <see cref="MaximumDecodedBlobBytes"/>，与应用路径强制的上限一致；每个元素至少按
/// <see cref="MinimumElementBytes"/> 计入该预算，因此不可信的数据块既不会造成无界工作量也不会造成无界分配。</para>
/// </remarks>
internal static class ZucchiniExecutableTypeScanner
{
    /// <summary>
    /// Upper bound on the decoded Zucchini container bytes one preflight scan may consume.
    /// <para>单次预扫描可消耗的已解码 Zucchini 容器字节上限。</para>
    /// </summary>
    /// <value>
    /// 256 MiB, identical to the per-operation ceiling the applying path enforces, so a container this scanner accepts
    /// is also one the applier will buffer. <para>256 MiB，与应用路径强制的逐操作上限一致，
    /// 因此本扫描器接受的容器也是应用器会缓冲的容器。</para>
    /// </value>
    internal const int MaximumDecodedBlobBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Smallest number of decoded bytes one Zucchini element can occupy, charged against the decode budget to bound
    /// the declared element count.
    /// <para>单个 Zucchini 元素可占据的最小已解码字节数，计入解码预算以给声明的元素数量设限。</para>
    /// </summary>
    /// <value>
    /// 54 bytes: the packed element header, the seven sub-stream length prefixes and the pool count, matching the
    /// divisor <see cref="ZucchiniPatchApplier"/> applies to its remaining byte count.
    /// <para>54 字节：紧凑元素头、七个子流长度前缀与池计数，与 <see cref="ZucchiniPatchApplier"/>
    /// 对其剩余字节数使用的除数一致。</para>
    /// </value>
    internal const int MinimumElementBytes = 54;

    private const int SubStreamCount = 7;
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Reports whether any operation in the sequence is a Zucchini operation and therefore needs a preflight scan.
    /// <para>报告序列中是否存在 Zucchini 操作，因而需要预扫描。</para>
    /// </summary>
    /// <param name="operations">Operations already classified as sequential by the caller.
    /// <para>调用方已归类为串行的操作。</para></param>
    /// <returns>
    /// <see langword="true"/> when at least one operation is <see cref="InstallOperation.Types.Type.Zucchini"/>.
    /// <para>当至少有一个操作是 <see cref="InstallOperation.Types.Type.Zucchini"/> 时为 <see langword="true"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operations"/> is <see langword="null"/>.
    /// <para><paramref name="operations"/> 为 <see langword="null"/>。</para></exception>
    internal static bool HasZucchiniOperation(IEnumerable<InstallOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            if (operation.Type == InstallOperation.Types.Type.Zucchini)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans every Zucchini operation of one partition, rejecting any element that declares a non-NoOp executable type.
    /// <para>扫描某分区的全部 Zucchini 操作，拒绝任何声明了非 NoOp 可执行类型的元素。</para>
    /// </summary>
    /// <param name="payload">Seekable payload stream the blobs are read from; its position is restored before return.
    /// <para>读取数据块所用的可定位载荷流；返回前其位置被还原。</para></param>
    /// <param name="operations">Partition operations to filter; non-Zucchini entries are ignored.
    /// <para>要过滤的分区操作；非 Zucchini 项被忽略。</para></param>
    /// <param name="baseOffset">Absolute byte offset in <paramref name="payload"/> where operation data begins.
    /// <para><paramref name="payload"/> 中操作数据起始的绝对字节偏移。</para></param>
    /// <param name="cancellationToken">Checked before each operation and inside each decode and skip chunk.
    /// <para>在每个操作之前以及每个解码与跳过块内部检查。</para></param>
    /// <exception cref="PayloadUnsupportedOperationException">An operation's blob range overflows the supported address
    /// range or leaves the payload stream.
    /// <para>某操作的数据块范围溢出所支持的地址范围或越出载荷流。</para></exception>
    /// <exception cref="ZucchiniException">A container is not valid strict Brotli, has bad magic or version, is
    /// truncated, exceeds <see cref="MaximumDecodedBlobBytes"/>, declares more elements than its byte budget allows,
    /// or declares an executable type other than NoOp.
    /// <para>某容器不是合法的严格 Brotli、魔数或版本不符、被截断、超过 <see cref="MaximumDecodedBlobBytes"/>、
    /// 声明的元素数量超出其字节预算，或声明了 NoOp 之外的可执行类型。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 已被取消。</para></exception>
    /// <remarks>
    /// A payload stream that cannot seek skips the scan entirely, because the blob range cannot be located; the
    /// applying path then rejects a non-NoOp element later, as it always did.
    /// <para>不可定位的载荷流会完全跳过本次扫描，因为无法定位数据块范围；随后应用路径仍会像以往一样拒绝
    /// 非 NoOp 元素。</para>
    /// </remarks>
    internal static void ScanOperations(Stream payload, IReadOnlyList<InstallOperation> operations, ulong baseOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(operations);
        if (!payload.CanSeek)
        {
            return;
        }

        var restore = payload.Position;
        try
        {
            foreach (var operation in operations)
            {
                if (operation.Type != InstallOperation.Types.Type.Zucchini)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ScanOperation(payload, operation, baseOffset, cancellationToken);
            }
        }
        finally
        {
            payload.Position = restore;
        }
    }

    /// <summary>
    /// Scans one Zucchini blob: locates it, decodes it, validates its header and walks the element-header table.
    /// <para>扫描单个 Zucchini 数据块：定位、解码、校验其头部并走一遍元素头表。</para>
    /// </summary>
    /// <param name="payload">Seekable payload stream holding the blob. <para>持有该数据块的可定位载荷流。</para></param>
    /// <param name="operation">Zucchini operation naming the blob range through its data offset and length.
    /// <para>通过其数据偏移与长度指明数据块范围的 Zucchini 操作。</para></param>
    /// <param name="baseOffset">Absolute byte offset in <paramref name="payload"/> where operation data begins.
    /// <para><paramref name="payload"/> 中操作数据起始的绝对字节偏移。</para></param>
    /// <param name="cancellationToken">Checked inside each decode and skip chunk. <para>在每个解码与跳过块内部检查。</para></param>
    /// <exception cref="PayloadUnsupportedOperationException">The blob range overflows the supported address range or
    /// leaves the payload stream. <para>数据块范围溢出所支持的地址范围或越出载荷流。</para></exception>
    /// <exception cref="ZucchiniException">The blob is not valid strict Brotli, the container has bad magic or version,
    /// is truncated, exceeds <see cref="MaximumDecodedBlobBytes"/>, declares more elements than its byte budget allows,
    /// or declares an executable type other than NoOp.
    /// <para>数据块不是合法的严格 Brotli、容器魔数或版本不符、被截断、超过 <see cref="MaximumDecodedBlobBytes"/>、
    /// 声明的元素数量超出其字节预算，或声明了 NoOp 之外的可执行类型。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 已被取消。</para></exception>
    internal static void ScanOperation(Stream payload, InstallOperation operation, ulong baseOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(operation);
        long start;
        long length;
        try
        {
            start = checked((long)checked(baseOffset + operation.DataOffset));
            length = checked((long)operation.DataLength);
        }
        catch (OverflowException ex)
        {
            throw new PayloadUnsupportedOperationException(
                "Zucchini operation data offset or length exceeds the supported range.",
                operation.Type.ToString(), ex);
        }

        if (start < 0 || length < 0 || start > payload.Length || length > payload.Length - start)
        {
            throw new PayloadUnsupportedOperationException(
                $"Zucchini operation data range [{start}, {start + length}) exceeds payload stream length {payload.Length}.",
                operation.Type.ToString());
        }

        using var window = new BoundedReadStream(payload, start, length, leaveOpen: true);
        using var decoder = new StrictBrotliReadStream(window);
        var reader = new DecodedReader(decoder, MaximumDecodedBlobBytes, cancellationToken);
        try
        {
            ScanContainer(reader);
        }
        catch (InvalidDataException ex)
        {
            throw new ZucchiniFormatException("Zucchini operation blob is not a valid Brotli stream.", ex);
        }
    }

    private static void ScanContainer(DecodedReader reader)
    {
        if (reader.ReadUInt32() != ZucchiniConstants.Magic)
        {
            throw new ZucchiniFormatException("Invalid Zucchini Zucc magic.");
        }

        if ((reader.ReadUInt32() & 0xffff) != ZucchiniConstants.Version)
        {
            throw new ZucchiniFormatException("Unsupported Zucchini major version.");
        }

        reader.Skip(ZucchiniConstants.HeaderByteSize - sizeof(uint) * 2);
        var elements = reader.ReadUInt32();
        if (elements > (uint)(MaximumDecodedBlobBytes / MinimumElementBytes))
        {
            throw new ZucchiniFormatException("Zucchini element count exceeds patch bounds.");
        }

        Span<byte> header = stackalloc byte[ZucchiniConstants.ElementHeaderByteSize];
        for (var index = 0u; index < elements; index++)
        {
            reader.ReadExactly(header);
            var executable = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
            if (executable != (uint)ZucchiniExecutableType.Raw)
            {
                throw new ZucchiniUnsupportedExecutableTypeException(
                    executable, BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]));
            }

            for (var subStream = 0; subStream < SubStreamCount; subStream++)
            {
                reader.Skip(reader.ReadUInt32());
            }

            if (reader.ReadUInt32() != 0)
            {
                throw new ZucchiniFormatException("NoOp elements cannot contain reference pools.");
            }
        }
    }

    /// <summary>
    /// Forward-only reader over a decoded Zucchini container that charges every consumed byte against a fixed budget
    /// and keeps one reusable buffer instead of materializing the container. It owns nothing; the caller disposes the
    /// decoder it reads from.
    /// <para>在已解码 Zucchini 容器上的只进读取器，把每个消耗字节计入固定预算，
    /// 并保留一个可复用缓冲区而不是把容器整体物化。它不拥有任何资源；解码器由调用方释放。</para>
    /// </summary>
    private sealed class DecodedReader(Stream input, long budget, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[BufferSize];
        private int _offset;
        private int _end;
        private long _consumed;

        internal void ReadExactly(Span<byte> target)
        {
            while (!target.IsEmpty)
            {
                if (_offset == _end && !Refill())
                {
                    throw new ZucchiniFormatException("Truncated Zucchini data.");
                }

                var count = Math.Min(target.Length, _end - _offset);
                _buffer.AsSpan(_offset, count).CopyTo(target);
                _offset += count;
                _consumed += count;
                target = target[count..];
            }
        }

        internal uint ReadUInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        internal void Skip(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > budget - _consumed)
            {
                throw new ZucchiniFormatException("Zucchini container exceeds the supported decoded size.");
            }

            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_offset == _end)
                {
                    if (!Refill())
                    {
                        throw new ZucchiniFormatException("Truncated Zucchini data.");
                    }

                    continue;
                }

                var chunk = (int)Math.Min(count, _end - _offset);
                _offset += chunk;
                _consumed += chunk;
                count -= chunk;
            }
        }

        private bool Refill()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_consumed >= budget)
            {
                throw new ZucchiniFormatException("Zucchini container exceeds the supported decoded size.");
            }

            _offset = 0;
            _end = input.Read(_buffer, 0, (int)Math.Min(_buffer.Length, budget - _consumed));
            return _end > 0;
        }
    }
}