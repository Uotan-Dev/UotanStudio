using System.Buffers.Binary;

namespace UotanStudio.Media.Audio;

/// <summary>
/// Bounded float32 PCM staging with drop-oldest semantics and no per-write allocations.
/// <para>有界 float32 PCM 暂存，超限丢弃最旧数据，每次写入不分配内存。</para>
/// </summary>
internal sealed class AudioPcmRingBuffer
{
    private readonly Lock _gate = new();
    private readonly byte[] _ring;
    private readonly byte[] _conversion;
    private readonly int _channels;
    private readonly int _capacityFrames;
    private int _readFrame;
    private int _count;

    /// <summary>Allocates the staging buffers once. <para>一次性分配暂存缓冲。</para></summary>
    internal AudioPcmRingBuffer(int channels, int capacityFrames = 19200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        _channels = channels;
        _capacityFrames = capacityFrames;
        _ring = new byte[checked(channels * capacityFrames * sizeof(float))];
        _conversion = new byte[_ring.Length];
    }

    /// <summary>
    /// Converts and stages PCM on the single producer thread; volume applies only to new samples.
    /// <para>在唯一生产线程转换并暂存 PCM；音量仅应用于新样本。</para>
    /// </summary>
    internal void Write(ReadOnlySpan<byte> pcm, float volume)
    {
        var inputAlign = _channels * sizeof(short);
        if (pcm.Length % inputAlign != 0) throw new ArgumentException("PCM length is not frame aligned.", nameof(pcm));
        var frames = Math.Min(pcm.Length / inputAlign, _capacityFrames);
        pcm = pcm[^checked(frames * inputAlign)..];
        for (var sample = 0; sample < frames * _channels; sample++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(pcm[(sample * 2)..]) / 32768f * volume;
            BinaryPrimitives.WriteSingleLittleEndian(_conversion.AsSpan(sample * 4), value);
        }
        var align = _channels * sizeof(float);
        lock (_gate)
        {
            var drop = Math.Max(0, _count + frames - _capacityFrames);
            _readFrame = (_readFrame + drop) % _capacityFrames;
            _count -= drop;
            var writeFrame = (_readFrame + _count) % _capacityFrames;
            var first = Math.Min(frames, _capacityFrames - writeFrame);
            _conversion.AsSpan(0, first * align).CopyTo(_ring.AsSpan(writeFrame * align));
            _conversion.AsSpan(first * align, (frames - first) * align).CopyTo(_ring);
            _count += frames;
        }
    }

    /// <summary>Consumes staged frames, filling underruns with silence. <para>消费暂存帧，欠载部分填充静音。</para></summary>
    internal void Read(Span<byte> destination)
    {
        var align = _channels * sizeof(float);
        if (destination.Length % align != 0) throw new ArgumentException("Output length is not frame aligned.", nameof(destination));
        lock (_gate)
        {
            var frames = Math.Min(destination.Length / align, _count);
            var first = Math.Min(frames, _capacityFrames - _readFrame);
            _ring.AsSpan(_readFrame * align, first * align).CopyTo(destination);
            _ring.AsSpan(0, (frames - first) * align).CopyTo(destination[(first * align)..]);
            _readFrame = (_readFrame + frames) % _capacityFrames;
            _count -= frames;
            destination[(frames * align)..].Clear();
        }
    }
}