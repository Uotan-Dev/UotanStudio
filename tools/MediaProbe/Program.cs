using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;

if (args is ["--probe"])
{
    Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    Console.WriteLine($"H264 backend available: {VideoDecoderFactory.IsSupported(VideoCodecId.H264)}");
    Console.WriteLine($"H265 backend available: {VideoDecoderFactory.IsSupported(VideoCodecId.H265)}");
    if (OperatingSystem.IsLinux())
    {
        string node = Environment.GetEnvironmentVariable("UOTAN_TEST_VA_RENDER_NODE") ?? "/dev/dri/renderD128";
        if (!File.Exists(node)) Console.WriteLine($"VA-API runtime not tested: missing {node}");
        else
        {
            using var va = new VaApiSession(node);
            Console.WriteLine($"VA API: {va.Version}");
            foreach (var capability in va.QueryCapabilities()) Console.WriteLine($"Profile {capability.Profile}, entrypoint {capability.Entrypoint}");
        }
    }
    return 0;
}
if (args is not ["--decode-black", var path])
{
    Console.Error.WriteLine("Usage: MediaProbe --probe | --decode-black <black-64x64.h264>");
    return 2;
}
byte[] packet = File.ReadAllBytes(path);
int count = 0;
using var decoder = VideoDecoderFactory.Create();
decoder.Initialize(VideoCodecId.H264, 64, 64, frame =>
{
    if (frame.Width != 64 || frame.Height != 64) throw new InvalidDataException("Unexpected dimensions.");
    for (int row = 0; row < 96; row++)
        foreach (byte pixel in frame.Span.Slice(row * frame.Stride, 64))
            if (pixel != (row < 64 ? 16 : 128)) throw new InvalidDataException("Unexpected decoded pixel.");
    count++;
});
for (int i = 0; i < 12; i++) decoder.Decode(packet);
if (count == 0) throw new InvalidDataException("No native frames produced.");
Console.WriteLine($"Verified {count} decoded 64x64 NV12 frames, all pixels match.");
return 0;
