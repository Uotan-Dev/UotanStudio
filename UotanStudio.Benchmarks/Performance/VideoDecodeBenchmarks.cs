using System.Collections.Concurrent;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>
/// Decode-rate baseline for the screen-mirroring video path, comparing NV12 plane readback with a
/// GPU color convert to BGRA.
/// <para>投屏视频路径的解码速率基线，比较 NV12 平面回读与 GPU 色彩转换到 BGRA。</para>
/// </summary>
[MemoryDiagnoser]
public class VideoDecodeBenchmarks
{
    private List<byte[]> _accessUnits = [];
    private int _width;
    private int _height;
    private readonly ConcurrentDictionary<string, double> _framesPerSecond = new();

    /// <summary>
    /// Loads the captured stream and splits it into Annex-B access units.
    /// <para>加载抓取的码流并按 Annex-B 访问单元切分。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The corpus path or size variable is missing, or the stream holds too few access units.
    /// <para>缺少语料路径或尺寸变量，或码流中访问单元过少。</para>
    /// </exception>
    [GlobalSetup]
    public void Setup()
    {
        var path = Environment.GetEnvironmentVariable("UOTAN_BENCH_H264");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "Set UOTAN_BENCH_H264 to a captured Annex-B H.264 stream; no committed fixture carries video bitstreams.");
        }

        var size = Environment.GetEnvironmentVariable("UOTAN_BENCH_H264_SIZE")?.Split('x', 'X');
        if (size is not { Length: 2 } || !int.TryParse(size[0], out _width) || !int.TryParse(size[1], out _height))
        {
            throw new InvalidOperationException("Set UOTAN_BENCH_H264_SIZE to the coded size, for example 1600x896.");
        }

        _accessUnits = SplitAccessUnits(File.ReadAllBytes(path));
        if (_accessUnits.Count < 30)
        {
            throw new InvalidOperationException($"Stream holds {_accessUnits.Count} access units; at least 30 are required.");
        }
    }

    /// <summary>
    /// Decodes the whole corpus with the GPU backend reading back NV12 planes.
    /// <para>使用 GPU 后端解码整段语料并回读 NV12 平面。</para>
    /// </summary>
    /// <returns>Decoded frame count. <para>解码帧数。</para></returns>
    [Benchmark(Baseline = true)]
    public int GpuNv12Readback() => Decode("GPU NV12", new VideoDecoderOptions
    {
        Backend = VideoDecoderBackend.MediaFoundationGpu,
        PreferNv12Output = true,
    });

    /// <summary>
    /// Decodes the whole corpus with the GPU backend converting to BGRA before the readback.
    /// <para>使用 GPU 后端在回读前先转成 BGRA 解码整段语料。</para>
    /// </summary>
    /// <returns>Decoded frame count. <para>解码帧数。</para></returns>
    [Benchmark]
    public int GpuBgraReadback() => Decode("GPU BGRA", new VideoDecoderOptions
    {
        Backend = VideoDecoderBackend.MediaFoundationGpu,
        PreferNv12Output = false,
    });

    /// <summary>
    /// Decodes the whole corpus with the CPU-readable Media Foundation backend.
    /// <para>使用可被 CPU 读取的 Media Foundation 后端解码整段语料。</para>
    /// </summary>
    /// <returns>Decoded frame count. <para>解码帧数。</para></returns>
    [Benchmark]
    public int MediaFoundationCpu() => Decode("MF CPU", new VideoDecoderOptions
    {
        Backend = VideoDecoderBackend.MediaFoundationCpu,
    });

    /// <summary>
    /// Writes the measured decode rate of each backend to the console after the run.
    /// <para>运行结束后把各后端实测的解码速率写入控制台。</para>
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var pair in _framesPerSecond.OrderByDescending(entry => entry.Value))
        {
            Console.WriteLine($"{pair.Key}: {pair.Value:F1} fps over {_accessUnits.Count} access units at {_width}x{_height}");
        }
    }

    private int Decode(string label, VideoDecoderOptions options)
    {
        var frames = 0;
        using IVideoDecoder decoder = VideoDecoderFactory.Create(VideoCodecId.H264, options);
        decoder.Initialize(VideoCodecId.H264, _width, _height, _ => frames++);
        var watch = Stopwatch.StartNew();
        long timestamp = 0;
        foreach (var unit in _accessUnits)
        {
            decoder.Decode(unit, timestamp);
            timestamp += 16_667;
            decoder.Poll();
        }

        decoder.Drain();
        watch.Stop();
        _framesPerSecond[label] = frames / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
        return frames;
    }

    /// <summary>
    /// Splits an Annex-B stream into single NAL access units, each rewritten with a four-byte start code.
    /// <para>把 Annex-B 码流拆成单个 NAL 访问单元，每段统一改写为四字节起始码。</para>
    /// </summary>
    /// <param name="stream">The raw Annex-B bytes. <para>原始 Annex-B 字节。</para></param>
    /// <returns>
    /// Access units in decode order; units shorter than a start code are dropped.
    /// <para>按解码顺序排列的访问单元；短于起始码的单元被丢弃。</para>
    /// </returns>
    private static List<byte[]> SplitAccessUnits(byte[] stream)
    {
        var units = new List<byte[]>();
        var starts = new List<(int Code, int Length)>();
        for (var index = 0; index + 2 < stream.Length; index++)
        {
            if (stream[index] != 0 || stream[index + 1] != 0)
            {
                continue;
            }

            if (stream[index + 2] == 1)
            {
                starts.Add((index, 3));
                index += 2;
            }
            else if (stream[index + 2] == 0 && index + 3 < stream.Length && stream[index + 3] == 1)
            {
                starts.Add((index, 4));
                index += 3;
            }
        }

        for (var entry = 0; entry < starts.Count; entry++)
        {
            var (code, codeLength) = starts[entry];
            var payloadStart = code + codeLength;
            var end = entry + 1 < starts.Count ? starts[entry + 1].Code : stream.Length;
            if (end <= payloadStart)
            {
                continue;
            }

            var unit = new byte[4 + end - payloadStart];
            unit[2] = 0;
            unit[3] = 1;
            stream.AsSpan(payloadStart, end - payloadStart).CopyTo(unit.AsSpan(4));
            units.Add(unit);
        }

        return units;
    }
}