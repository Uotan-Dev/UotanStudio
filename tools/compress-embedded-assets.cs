#:property ManagePackageVersionsCentrally=false
#:package ZstdSharp.Port@0.8.8

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

const int Level = 19;

var arguments = args;
var checkOnly = arguments.Contains("--check");
var root = FindRepositoryRoot();
var fontsSource = ReadOption("--fonts-source") ?? Path.Combine(root, "reference", "fonts-source");
var driverSource = Path.Combine(root, "reference", "driver-pkg");
var assetsRoot = Path.Combine(root, "UotanStudio.Assets", "Assets");
var driversTarget = Path.Combine(assetsRoot, "drivers");

var specs = new BundleSpec[]
{
    new("fonts", Path.Combine(assetsRoot, "fonts"), fontsSource, "*.ttf", Generated: false, Optional: false),
    new("ohos/device", Path.Combine(assetsRoot, "ohos", "device"), null, "*.so", Generated: false, Optional: false),
    new("drivers/rockusb", Path.Combine(driversTarget, "rockusb"), null, "rockusb.*", Generated: false, Optional: false),
    new("drivers/mediatek-vcom", Path.Combine(driversTarget, "mediatek-vcom"),
        Path.Combine(driverSource, "mediatek-vcom-whql-wu-catalog", "small"), "*", Generated: false, Optional: false,
        [
            new("cdc-acm.cat", "cdc-acm.cat"),
            new("cdc-acm.inf", "cdc-acm.inf"),
            new("x64__usb2ser.sys", "x64/usb2ser.sys"),
            new("x86__usb2ser.sys", "x86/usb2ser.sys"),
        ]),
    new("drivers/mediatek-full", Path.Combine(driversTarget, "mediatek-full"),
        Path.Combine(driverSource, "mediatek-vcom-whql-wu-catalog", "big"), "*", Generated: false, Optional: false,
        [
            new("amd64__WUDFUpdate_01009.dll", "amd64/WUDFUpdate_01009.dll"),
            new("amd64__WdfCoInstaller01009.dll", "amd64/WdfCoInstaller01009.dll"),
            new("amd64__winusbcoinstaller2.dll", "amd64/winusbcoinstaller2.dll"),
            new("android_winusb.inf", "android_winusb.inf"),
            new("androidwinusb.cat", "androidwinusb.cat"),
            new("cdc-acm.cat", "cdc-acm.cat"),
            new("cdc-acm.inf", "cdc-acm.inf"),
            new("mtk_isd_xp32.cat", "mtk_isd_xp32.cat"),
            new("mtk_isd_xp64.cat", "mtk_isd_xp64.cat"),
            new("mtkmbim7_x64.cat", "mtkmbim7_x64.cat"),
            new("mtkmbim7_x64.inf", "mtkmbim7_x64.inf"),
            new("mtkmbim7_x64.sys", "mtkmbim7_x64.sys"),
            new("mtkmbimv_x64.cat", "mtkmbimv_x64.cat"),
            new("mtkmbimv_x64.inf", "mtkmbimv_x64.inf"),
            new("mtkmbimv_x64.sys", "mtkmbimv_x64.sys"),
            new("mtkmbimx_x64.cat", "mtkmbimx_x64.cat"),
            new("mtkmbimx_x64.inf", "mtkmbimx_x64.inf"),
            new("mtkmbimx_x64.sys", "mtkmbimx_x64.sys"),
            new("tetherxp.inf", "tetherxp.inf"),
            new("wpdmtp.cat", "wpdmtp.cat"),
            new("wpdmtp.inf", "wpdmtp.inf"),
            new("x64__usb2ser.sys", "x64/usb2ser.sys"),
        ]),
    new("drivers/google-usb", Path.Combine(driversTarget, "google-usb"),
        Path.Combine(driverSource, "google-usb-driver-r13"), "*", Generated: false, Optional: false,
        [
            new("amd64__WUDFUpdate_01009.dll", "amd64/WUDFUpdate_01009.dll"),
            new("amd64__WdfCoInstaller01009.dll", "amd64/WdfCoInstaller01009.dll"),
            new("amd64__winusbcoinstaller2.dll", "amd64/winusbcoinstaller2.dll"),
            new("android_winusb.inf", "android_winusb.inf"),
            new("androidwinusb86.cat", "androidwinusb86.cat"),
            new("androidwinusba64.cat", "androidwinusba64.cat"),
        ]),
    new("drivers/rockchip-adb", Path.Combine(driversTarget, "rockchip-adb"),
        Path.Combine(driverSource, "adbdriver-rockchip-v5.14"), "*", Generated: false, Optional: false,
        [
            new("NOTICE.txt", "amd64/NOTICE.txt"),
            new("amd64__WUDFUpdate_01009.dll", "amd64/WUDFUpdate_01009.dll"),
            new("amd64__WdfCoInstaller01009.dll", "amd64/WdfCoInstaller01009.dll"),
            new("amd64__winusbcoinstaller2.dll", "amd64/winusbcoinstaller2.dll"),
            new("android_winusb.inf", "android_winusb.inf"),
            new("androidwinusb86.cat", "androidwinusb86.cat"),
            new("androidwinusba64.cat", "androidwinusba64.cat"),
        ]),
    new("drivers/qualcomm-qud", Path.Combine(driversTarget, "qualcomm-qud"),
        Path.Combine(driverSource, "qualcomm-qud-win-v1.00.95.6"), "*", Generated: false, Optional: false,
        [
            new("filter__amd64__qcusbfilter.sys", "filter/amd64/qcusbfilter.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("filter__arm64__qcusbfilter.sys", "filter/arm64/qcusbfilter.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("ndis__amd64__qcusbwwan.sys", "ndis/amd64/qcusbwwan.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("ndis__arm64__qcusbwwan.sys", "ndis/arm64/qcusbwwan.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcadb.cat", "qcadb.cat", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcadb.inf", "qcadb.inf", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcfilter.cat", "qcfilter.cat", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcfilter.inf", "qcfilter.inf", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcwdfmdm.inf", "qcwdfmdm.inf", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcwdfser.inf", "qcwdfser.inf", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcwdfserial.cat", "qcwdfserial.cat", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcwwan.cat", "qcwwan.cat", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qcwwan.inf", "qcwwan.inf", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qdbusb.cat", "qdbusb.cat", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qdbusb.inf", "qdbusb.inf", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qdss__amd64__qdbusb.sys", "qdss/amd64/qdbusb.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("qdss__arm64__qdbusb.sys", "qdss/arm64/qdbusb.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("wdfserial__amd64__qcwdfserial.sys", "wdfserial/amd64/qcwdfserial.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
            new("wdfserial__arm64__qcwdfserial.sys", "wdfserial/arm64/qcwdfserial.sys", "qud-win-v1.00.95.6_64bit_signed.zip"),
        ]),
    new("drivers/silabs-cp210x", Path.Combine(driversTarget, "silabs-cp210x"),
        Path.Combine(driverSource, "silabs-cp210x"), "*", Generated: false, Optional: false,
        [
            new("SLAB_License_Agreement_VCP_Windows.txt", "SLAB_License_Agreement_VCP_Windows.txt", "CP210x_Universal_Windows_Driver.zip"),
            new("arm__silabser.sys", "arm/silabser.sys", "CP210x_Universal_Windows_Driver.zip"),
            new("arm64__silabser.sys", "arm64/silabser.sys", "CP210x_Universal_Windows_Driver.zip"),
            new("silabser.cat", "silabser.cat", "CP210x_Universal_Windows_Driver.zip"),
            new("silabser.inf", "silabser.inf", "CP210x_Universal_Windows_Driver.zip"),
            new("x64__silabser.sys", "x64/silabser.sys", "CP210x_Universal_Windows_Driver.zip"),
            new("x86__silabser.sys", "x86/silabser.sys", "CP210x_Universal_Windows_Driver.zip"),
        ]),
    new("drivers/ftdi-cdm", Path.Combine(driversTarget, "ftdi-cdm"),
        Path.Combine(driverSource, "ftdi-cdm"), "*", Generated: false, Optional: false,
        [
            new("Image__FTDIBUS.inf", "Image/FTDIBUS.inf", "CDM-v2.12.36.20-Universal-x64.zip"),
            new("Image__FTDIBUS.sys", "Image/FTDIBUS.sys", "CDM-v2.12.36.20-Universal-x64.zip"),
            new("Image__FTDIPORT.inf", "Image/FTDIPORT.inf", "CDM-v2.12.36.20-Universal-x64.zip"),
            new("Image__FTSER2K.sys", "Image/FTSER2K.sys", "CDM-v2.12.36.20-Universal-x64.zip"),
            new("Image__ftdibus.cat", "Image/ftdibus.cat", "CDM-v2.12.36.20-Universal-x64.zip"),
            new("Image__ftdiport.cat", "Image/ftdiport.cat", "CDM-v2.12.36.20-Universal-x64.zip"),
        ]),
    new("delivery", Path.Combine(assetsRoot, "delivery"), null, "update-binary-arm64", Generated: false, Optional: false),
    new("firmware/magic", Path.Combine(assetsRoot, "firmware", "magic"), null, "*.zst", Generated: true, Optional: false),
    new("scrcpy", Path.Combine(assetsRoot, "scrcpy"), null, "scrcpy-server.jar", Generated: false, Optional: true),
};

var rows = new List<Row>();
var failures = 0;

foreach (var spec in specs)
{
    failures += ProcessBundle(spec);
}

var manifestPath = Path.Combine(assetsRoot, "manifest.tsv");
if (checkOnly)
{
    failures += VerifyManifest(manifestPath, rows);
}
else
{
    WriteManifest(manifestPath, rows);
}

Console.WriteLine(failures == 0
    ? (checkOnly ? "asset manifest check: OK" : "asset compression: OK")
    : $"asset manifest: {failures} failure(s)");
return failures == 0 ? 0 : 1;

int ProcessBundle(BundleSpec spec)
{
    var failures = 0;
    var sourceDirectory = spec.SourceDirectory ?? spec.TargetDirectory;
    Directory.CreateDirectory(spec.TargetDirectory);

    var names = new List<string>();
    if (spec.Generated)
    {
        foreach (var frame in Directory.EnumerateFiles(spec.TargetDirectory, spec.Pattern))
        {
            if (frame.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileName(frame)[..^4]);
            }
        }
    }
    else if (spec.Payloads is { } declared)
    {
        foreach (var payload in declared)
        {
            names.Add(payload.PayloadName);
        }
    }
    else if (Directory.Exists(sourceDirectory))
    {
        foreach (var payloadPath in Directory.EnumerateFiles(sourceDirectory, spec.Pattern))
        {
            if (payloadPath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(Path.GetFileName(payloadPath));
        }
    }

    names.Sort(StringComparer.Ordinal);
    if (names.Count == 0)
    {
        if (spec.Optional)
        {
            Console.WriteLine($"{spec.Key,-18} skipped (no payload)");
            if (!checkOnly)
            {
                RemoveStaleFrames(spec, []);
            }

            return 0;
        }

        if (checkOnly && Directory.Exists(spec.TargetDirectory)
            && Directory.EnumerateFiles(spec.TargetDirectory, "*.zst").Any())
        {
            foreach (var frame in Directory.EnumerateFiles(spec.TargetDirectory, "*.zst"))
            {
                names.Add(Path.GetFileName(frame)[..^4]);
            }

            names.Sort(StringComparer.Ordinal);
        }
        else
        {
            Console.Error.WriteLine($"{spec.Key}: no payload matched '{spec.Pattern}' in '{sourceDirectory}'");
            return 1;
        }
    }

    foreach (var name in names)
    {
        var framePath = Path.Combine(spec.TargetDirectory, name + ".zst");
        byte[] payload;
        if (spec.Generated)
        {
            if (!File.Exists(framePath))
            {
                Console.Error.WriteLine($"{spec.Key}: generated frame '{framePath}' is missing");
                failures++;
                continue;
            }

            payload = Unwrap(File.ReadAllBytes(framePath), framePath);
            rows.Add(Row(spec.Key, name, payload.Length, payload, "generated"));
            continue;
        }

        var declaredSource = spec.Payloads?.FirstOrDefault(
            payload => string.Equals(payload.PayloadName, name, StringComparison.Ordinal));
        var source = TryReadPayload(sourceDirectory, name, declaredSource);
        if (source is not null)
        {
            if (checkOnly)
            {
                if (!File.Exists(framePath))
                {
                    Console.Error.WriteLine($"{spec.Key}: frame '{framePath}' is missing");
                    failures++;
                    continue;
                }

                var decoded = Unwrap(File.ReadAllBytes(framePath), framePath);
                if (!decoded.AsSpan().SequenceEqual(source))
                {
                    Console.Error.WriteLine($"{spec.Key}: frame '{framePath}' does not decode to '{name}'");
                    failures++;
                    continue;
                }
            }
            else
            {
                var compressed = new Compressor(Level).Wrap(source).ToArray();
                var roundTrip = Unwrap(compressed, framePath);
                if (!roundTrip.AsSpan().SequenceEqual(source))
                {
                    Console.Error.WriteLine($"{spec.Key}: round trip mismatch for '{name}'");
                    failures++;
                    continue;
                }

                File.WriteAllBytes(framePath, compressed);
                Console.WriteLine(
                    $"{spec.Key}/{name,-38} {source.Length,10:N0} -> {compressed.Length,10:N0} ({compressed.Length * 100.0 / source.Length,5:F1}%)");
                rows.Add(Row(spec.Key, name, source.Length, source, "source"));
                continue;
            }
        }
        else if (checkOnly && File.Exists(framePath))
        {
            Console.WriteLine($"{spec.Key}/{name}: payload source missing, verifying the frame only");
            source = Unwrap(File.ReadAllBytes(framePath), framePath);
        }
        else
        {
            Console.Error.WriteLine($"{spec.Key}: payload source '{name}' is missing");
            failures++;
            continue;
        }

        rows.Add(Row(spec.Key, name, source.Length, source, "source"));
    }

    if (!checkOnly)
    {
        RemoveStaleFrames(spec, names);
    }

    return failures;
}

byte[]? TryReadPayload(string sourceDirectory, string name, PayloadSource? declared)
{
    var relativePath = declared?.SourcePath ?? name;
    if (declared?.Archive is { } archiveName)
    {
        var archivePath = Path.Combine(sourceDirectory, archiveName);
        if (!File.Exists(archivePath))
        {
            return null;
        }

        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName.Replace('\\', '/'), relativePath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    var path = Path.Combine(sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(path) ? File.ReadAllBytes(path) : null;
}

void RemoveStaleFrames(BundleSpec spec, List<string> names)
{
    foreach (var frame in Directory.EnumerateFiles(spec.TargetDirectory, "*.zst"))
    {
        var name = Path.GetFileName(frame)[..^4];
        if (!names.Contains(name, StringComparer.Ordinal))
        {
            Console.WriteLine($"{spec.Key}: removing stale frame '{Path.GetFileName(frame)}'");
            File.Delete(frame);
        }
    }
}

int VerifyManifest(string manifestPath, List<Row> rows)
{
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"manifest '{manifestPath}' is missing");
        return 1;
    }

    var expected = BuildManifestText(rows);
    var actual = File.ReadAllText(manifestPath, new UTF8Encoding(false));
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("manifest.tsv is out of date; re-run without --check to regenerate it");
        return 1;
    }

    Console.WriteLine("manifest.tsv is up to date");
    return 0;
}

void WriteManifest(string manifestPath, List<Row> rows)
{
    File.WriteAllText(manifestPath, BuildManifestText(rows), new UTF8Encoding(false));
    Console.WriteLine($"wrote {Path.GetRelativePath(root, manifestPath)}");
}

string BuildManifestText(List<Row> rows)
{
    var text = new StringBuilder();
    text.Append("# Generated by tools/compress-embedded-assets.cs; do not edit by hand.\n");
    text.Append("# <bundle>\t<path>\t<uncompressedLength>\t<lowercaseSha256>\t<encoding>\t<origin>\n");
    foreach (var row in rows.OrderBy(static row => row.Bundle, StringComparer.Ordinal)
                 .ThenBy(static row => row.Path, StringComparer.Ordinal))
    {
        text.Append(row.Bundle).Append('\t')
            .Append(row.Path).Append('\t')
            .Append(row.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(row.Sha256).Append('\t')
            .Append("zstd").Append('\t')
            .Append(row.Origin).Append('\n');
    }

    return text.ToString();
}

Row Row(string bundle, string path, long length, byte[] payload, string origin) =>
    new(bundle, path, length, Convert.ToHexStringLower(SHA256.HashData(payload)), origin);

byte[] Unwrap(byte[] frame, string framePath)
{
    try
    {
        return new Decompressor().Unwrap(frame).ToArray();
    }
    catch (ZstdException error)
    {
        throw new InvalidOperationException($"'{framePath}' is not a zstd frame: {error.Message}", error);
    }
}

string? ReadOption(string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "UotanStudio.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException(
        "Run this script from inside the UotanStudio repository (UotanStudio.slnx not found).");
}

internal sealed record BundleSpec(
    string Key,
    string TargetDirectory,
    string? SourceDirectory,
    string Pattern,
    bool Generated,
    bool Optional,
    IReadOnlyList<PayloadSource>? Payloads = null);

internal sealed record PayloadSource(string PayloadName, string SourcePath, string? Archive = null);

internal sealed record Row(string Bundle, string Path, long Length, string Sha256, string Origin);
