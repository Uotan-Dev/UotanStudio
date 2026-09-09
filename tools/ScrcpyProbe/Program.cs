using System.Diagnostics;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.Media.Decoding;

if (args.Length < 3) { Console.Error.WriteLine("Usage: ScrcpyProbe <serial> <server.jar> <output-dir> [h264|h265] [--performance]"); return 2; }
Directory.CreateDirectory(args[2]);
var server = new AdbServerServices(args[0]);
bool performance = args.Length > 4 && args[4] == "--performance";
var options = new ScrcpyServerOptions { VideoCodec = args.Length > 3 ? args[3] : "h264", MaxSize = performance ? 0 : 1024, MaxFps = performance ? 60 : 30, StayAwake = false, LogLevel = "debug" };
Console.WriteLine($"Serial={args[0]}; scid={options.Scid:x8}; codec={options.VideoCodec}");
server.Push(args[1], ScrcpyConstants.ServerDevicePath);
using Stream shell = server.Open("shell:" + options.BuildShellCommand());
var log = new StringBuilder();
var logTask = Task.Run(() =>
{
    try
    {
        byte[] bytes = new byte[4096]; int count;
        while ((count = shell.Read(bytes)) > 0) { string text = Encoding.UTF8.GetString(bytes, 0, count); lock (log) log.Append(text); Console.Write(text); }
    }
    catch (Exception ex) { lock (log) log.AppendLine(ex.Message); }
});
try
{
    using var client = new ScrcpyClient(server, options);
    Thread.Sleep(700);
    client.Connect(5000);
    var meta = client.VideoMetadata!;
    Console.WriteLine($"Handshake: {client.DeviceName}; {meta.CodecName}; {meta.Width}x{meta.Height}; control={client.IsControlConnected}");
    using var decoder = VideoDecoderFactory.Create();
    using var capture = File.Create(Path.Combine(args[2], "capture." + options.VideoCodec));
    int decoded = 0, packets = 0, configs = 0;
    long firstMs = -1, bytesRead = 0;
    long conversionTicks = 0, decodeTicks = 0, readTicks = 0;
    var hashes = new HashSet<string>();
    byte[]? final = null;
    int finalWidth = 0, finalHeight = 0;
    var stopwatch = Stopwatch.StartNew();
    decoder.Initialize(options.VideoCodec == "h264" ? VideoCodecId.H264 : VideoCodecId.H265, meta.Width, meta.Height, frame =>
    {
        decoded++;
        if (firstMs < 0) firstMs = stopwatch.ElapsedMilliseconds;
        if (!performance) hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame.Span)));
        if (final is null || finalWidth != frame.Width || finalHeight != frame.Height) final = new byte[frame.Width * frame.Height * 4];
        finalWidth = frame.Width; finalHeight = frame.Height;
        long conversionStart = Stopwatch.GetTimestamp();
        Nv12ToBgraConverter.Convert(frame, final);
        conversionTicks += Stopwatch.GetTimestamp() - conversionStart;
        if (decoded == 1) SaveBmp(Path.Combine(args[2], "first-frame.bmp"), final, finalWidth, finalHeight);
    });
    byte[]? configuration = null;
    bool homeSent = false, panelSent = false, backSent = false;
    long nextAnimation = 0;
    bool expanded = false;
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(18))
    {
        if (performance && stopwatch.ElapsedMilliseconds >= nextAnimation)
        {
            client.SendControl([expanded ? ScrcpyConstants.ControlTypeCollapsePanels : ScrcpyConstants.ControlTypeExpandNotificationPanel]);
            expanded = !expanded;
            nextAnimation = stopwatch.ElapsedMilliseconds + 600;
        }
        if (!performance && !homeSent && stopwatch.ElapsedMilliseconds > 2000)
        {
            client.SendControl(ScrcpyControlMessageWriter.InjectKeyCode(0, 3, 0, 0));
            client.SendControl(ScrcpyControlMessageWriter.InjectKeyCode(1, 3, 0, 0)); homeSent = true;
            Console.WriteLine("Control: HOME");
        }
        if (!performance && !panelSent && stopwatch.ElapsedMilliseconds > 6000) { client.SendControl([5]); panelSent = true; Console.WriteLine("Control: expand notifications"); }
        if (!performance && !backSent && stopwatch.ElapsedMilliseconds > 12000)
        {
            client.SendControl(ScrcpyControlMessageWriter.InjectKeyCode(0, 4, 0, 0));
            client.SendControl(ScrcpyControlMessageWriter.InjectKeyCode(1, 4, 0, 0)); backSent = true;
            Console.WriteLine("Control: BACK");
        }
        long readStart = Stopwatch.GetTimestamp();
        ScrcpyVideoFrame? packet = client.ReadFrame(1000);
        readTicks += Stopwatch.GetTimestamp() - readStart;
        if (packet is null) continue;
        packets++; bytesRead += packet.Data.Length;
        if (!performance) capture.Write(packet.Data);
        if (packet.IsCodecConfig) { configs++; configuration = packet.Data; continue; }
        long decodeStart = Stopwatch.GetTimestamp();
        if (configuration is not null)
        {
            byte[] combined = new byte[configuration.Length + packet.Data.Length]; configuration.CopyTo(combined, 0); packet.Data.CopyTo(combined, configuration.Length);
            decoder.Decode(combined, checked(packet.PresentationTimestampMicroseconds * 10)); configuration = null;
        }
        else decoder.Decode(packet.Data, checked(packet.PresentationTimestampMicroseconds * 10));
        decodeTicks += Stopwatch.GetTimestamp() - decodeStart;
    }
    if (performance) client.SendControl([ScrcpyConstants.ControlTypeCollapsePanels]);
    int beforeDrain = decoded;
    decoder.Drain();
    if (final is not null) SaveBmp(Path.Combine(args[2], "last-frame.bmp"), final, finalWidth, finalHeight);
    string summary = $"codec={options.VideoCodec}\nsize={meta.Width}x{meta.Height}\npackets={packets}\nconfigs={configs}\nbytes={bytesRead}\ndecodedBeforeDrain={beforeDrain}\ndecodedAfterDrain={decoded}\ndistinctHashes={hashes.Count}\nfirstFrameAfterHandshakeMs={firstMs}\nelapsedMs={stopwatch.ElapsedMilliseconds}\n";
    summary += $"performance={performance}\nconversionMsPerFrame={conversionTicks * 1000.0 / Stopwatch.Frequency / Math.Max(decoded, 1):F3}\ndecodeIncludingCallbackMsPerFrame={decodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(decoded, 1):F3}\nreadWaitMs={readTicks * 1000.0 / Stopwatch.Frequency:F1}\n";
    Console.WriteLine(summary); File.WriteAllText(Path.Combine(args[2], "result.txt"), summary);
    if (decoded < 5 || !performance && hashes.Count < 2) throw new InvalidDataException("Insufficient decoded frames or no changing video.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"scrcpy probe failed: {exception.Message}");
    File.WriteAllText(Path.Combine(args[2], "failure.txt"), exception.ToString());
    return 1;
}
finally
{
    shell.Dispose();
    logTask.Wait(2000);
    lock (log) File.WriteAllText(Path.Combine(args[2], "server.log"), log.ToString());
}

static void SaveBmp(string path, byte[] bgra, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0x4d42); writer.Write(54 + bgra.Length); writer.Write(0); writer.Write(54);
    writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
    writer.Write(0); writer.Write(bgra.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(bgra);
}
