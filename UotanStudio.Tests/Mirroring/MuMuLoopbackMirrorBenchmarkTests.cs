using System.Diagnostics;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Devices;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Tcp;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

/// <summary>
/// MuMu 模拟器回环 5555 端口投屏吞吐门控实测：真实 scrcpy 会话 + Media Foundation GPU 解码，
/// 统计接收、解码与上传速率；未设置 UOTAN_MUMU_LOOPBACK=1 时跳过。
/// </summary>
public class MuMuLoopbackMirrorBenchmarkTests
{
    private const string JarPath = "../../../../../UotanStudio/Assets/scrcpy-server.jar";

    private static string ResolveJarPath()
    {
        var candidate = Path.GetFullPath(JarPath);
        if (File.Exists(candidate)) return candidate;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UotanStudio", "Assets", "scrcpy-server.jar")))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("scrcpy-server.jar not found under the repository root.");
        return Path.Combine(dir.FullName, "UotanStudio", "Assets", "scrcpy-server.jar");
    }

    public static bool LoopbackEnabled =>
        Environment.GetEnvironmentVariable("UOTAN_MUMU_LOOPBACK") == "1";

    [Fact(Skip = "Requires UOTAN_MUMU_LOOPBACK=1 and a MuMu emulator listening on 127.0.0.1:5555.", SkipUnless = nameof(LoopbackEnabled))]
    public async Task MirrorLoop_MuMuLoopback5555_ReportsThroughput()
    {
        var ct = TestContext.Current.CancellationToken;
        using var adb = new AdbSession(new AdbTcpTransport("127.0.0.1", 5555), AdbAuthentication.LoadOrCreate());
        await adb.ConnectAsync(ct);

        var options = new ScrcpyServerOptions { Control = false, Audio = false, MaxFps = 60 };
        ScrcpyServerManager.StopServer(adb);
        ScrcpyServerManager.PushServer(adb, ResolveJarPath());
        using var shell = ScrcpyServerManager.StartServer(adb, options);
        using var client = new ScrcpyClient(adb, options);
        using var session = new ManagedMirrorSession(client, shell.AsServiceStream(), adb, logSink: _ => { });
        session.Connect();

        Assert.NotNull(session.VideoMetadata);
        var metadata = session.VideoMetadata!;
        var codec = metadata.CodecName switch
        {
            "h265" => VideoCodecId.H265,
            "av1" => VideoCodecId.Av1,
            _ => VideoCodecId.H264,
        };

        int received = 0, decoded = 0, uploaded = 0;
        long receivedBytes = 0;
        var decodeStopwatch = new Stopwatch();
        long decodeTicks = 0, convertTicks = 0;
        long lastReport = 0;
        var receiveRate = new Queue<(long Ticks, int Frames)>();
        var sw = Stopwatch.StartNew();

        using var barrier = new ManualResetEventSlim(false);
        using var decoderReady = new ManualResetEventSlim(false);
        IVideoDecoder? decoder = null;
        Exception? decodeFault = null;
        string? backendName = null;

        var decodeThread = new Thread(() =>
        {
            try
            {
                // The MF GPU decoder binds to its owning thread; create and initialize it here.
                // MF GPU 解码器绑定所属线程，因此在此线程上创建并初始化。
                var owned = VideoDecoderFactory.Create(codec, new VideoDecoderOptions());
                owned.Initialize(codec, metadata.Width, metadata.Height, frame =>
                {
                    Interlocked.Increment(ref decoded);
                    var convertStart = Stopwatch.GetTimestamp();
                    if (frame is PointerDecodedVideoFrame or GpuDecodedVideoFrame)
                    {
                        // The Windows GPU path delivers BGRA; one full-frame copy emulates the upload.
                        // Windows GPU 路径交付 BGRA；一次整帧复制用于模拟上传。
                        var bgra = new byte[frame.Length];
                        frame.Span.CopyTo(bgra);
                    }
                    else
                    {
                        var bgra = new byte[Nv12ToBgraConverter.GetBgraBufferSize(frame.Width, frame.Height)];
                        Nv12ToBgraConverter.Convert(in frame, bgra);
                    }
                    Interlocked.Increment(ref uploaded);
                    Interlocked.Add(ref convertTicks, Stopwatch.GetTimestamp() - convertStart);
                });
                decoder = owned;
                backendName = VideoDecoderFactory.GetBackendName(owned);
                decoderReady.Set();
                byte[]? configuration = null;
                // Mimic the production loop's initial Poll: pump the startup NeedInput event before
                // the first Decode instead of waiting for a read timeout that may never happen.
                // 模拟生产循环的首次 Poll：在首个 Decode 前泵出启动 NeedInput 事件，
                // 而不是等待可能永远不会发生的读超时。
                owned.Poll();
                while (!barrier.IsSet)
                {
                    ScrcpyVideoFrame? packet;
                    try { packet = session.ReadFrame(500); }
                    catch (EndOfStreamException) { break; }
                    if (packet is null) { owned.Poll(); continue; }
                    if (packet.IsCodecConfig) { configuration = packet.Data; continue; }
                    Interlocked.Increment(ref received);
                    Interlocked.Add(ref receivedBytes, packet.Data.Length);
                    var start = Stopwatch.GetTimestamp();
                    decodeStopwatch.Start();
                    if (configuration is null)
                    {
                        owned.Decode(packet.Data, packet.PresentationTimestampMicroseconds * 10);
                    }
                    else
                    {
                        // Match the production decode loop: prepend the codec config to the first keyframe.
                        // 与生产解码循环一致：把编码配置拼接到首个关键帧前。
                        var length = configuration.Length + packet.Data.Length;
                        var combined = new byte[length];
                        configuration.CopyTo(combined, 0);
                        packet.Data.CopyTo(combined, configuration.Length);
                        owned.Decode(combined, packet.PresentationTimestampMicroseconds * 10);
                        configuration = null;
                    }
                    // Async MFTs queue events that only surface through Poll; without it the
                    // input-event wait inside Decode starves and blocks the receive loop.
                    // 异步 MFT 的事件只能经 Poll 提取；缺少它会让 Decode 内的输入事件等待饿死并阻塞接收循环。
                    owned.Poll();
                    decodeStopwatch.Stop();
                    Interlocked.Add(ref decodeTicks, Stopwatch.GetTimestamp() - start);
                }
                owned.Drain();
                owned.Dispose();
            }
            catch (Exception error)
            {
                decodeFault = error;
                decoderReady.Set();
            }
        });
        decodeThread.Start();
        Assert.True(decoderReady.Wait(15000, ct), "The decode thread did not initialize in time.");
        if (decodeFault is not null) throw decodeFault;
        Assert.NotNull(decoder);

        // Measure for 10 seconds, print a 1-second rate per second like the UI panel.
        while (sw.ElapsedMilliseconds < 10_000 && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            if (decodeFault is not null) break;
            var now = sw.ElapsedMilliseconds;
            receiveRate.Enqueue((now, Volatile.Read(ref received)));
            if (receiveRate.Count > 2) receiveRate.Dequeue();
            var (prevTicks, prevFrames) = receiveRate.Peek();
            var rate = (int)((Volatile.Read(ref received) - prevFrames) * 1000.0 / Math.Max(1, now - prevTicks));
            lastReport = rate;
        }

        barrier.Set();
        decodeThread.Join(3000);

        var seconds = Math.Max(0.5, sw.ElapsedMilliseconds / 1000.0);
        var output = $"""
            MuMu loopback mirror benchmark ({metadata.CodecName} {metadata.Width}x{metadata.Height}, backend {backendName ?? "?"}):
              receive : {Volatile.Read(ref received) / seconds:F1} fps, {Volatile.Read(ref receivedBytes) / seconds / 1_000_000.0:F2} Mbps (1s window last {lastReport})
              decode  : {Volatile.Read(ref decoded) / seconds:F1} fps, decode CPU {decodeStopwatch.Elapsed.TotalMilliseconds / seconds:F1} ms/s
              convert : {Volatile.Read(ref uploaded) / seconds:F1} fps, convert CPU {new TimeSpan(convertTicks).TotalMilliseconds / seconds:F1} ms/s
            """;
        TestContext.Current.TestOutputHelper?.WriteLine(output);
        // Test-host output is not always echoed on pass; persist the metrics for the benchmark report.
        // 测试宿主通过时未必回显输出；把指标落盘以便生成基准报告。
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mumu-mirror-benchmark.txt"), output);
        if (decodeFault is not null) TestContext.Current.TestOutputHelper?.WriteLine($"decode fault: {decodeFault}");

        Assert.True(Volatile.Read(ref received) > 30, $"Too few frames received in 10 s: {Volatile.Read(ref received)}.");
        Assert.True(Volatile.Read(ref decoded) > 30, $"Too few frames decoded in 10 s: {Volatile.Read(ref decoded)}{(decodeFault is null ? "" : $"; decode thread fault: {decodeFault.Message}")}.");
    }
}