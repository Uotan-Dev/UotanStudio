using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Independent Android apksig verification. <para>使用 Android apksig 独立验证签名。</para></summary>
public sealed class ApkSigningInteropTests
{
    [Theory]
    [InlineData("rsa", "v1")]
    [InlineData("rsa", "v2")]
    [InlineData("rsa", "v3")]
    [InlineData("rsa", "all")]
    [InlineData("ec", "v1")]
    [InlineData("ec", "v2")]
    [InlineData("ec", "v3")]
    [InlineData("ec", "all")]
    [InlineData("rsa4096", "all")]
    [InlineData("ec384", "all")]
    public async Task Sign_UpstreamAcceptsAndRejectsTampering(string kind, string scheme)
    {
        var jar = Environment.GetEnvironmentVariable("UOTAN_APKSIG_JAR");
        var java = Environment.GetEnvironmentVariable("UOTAN_JAVA");
        if (!File.Exists(jar) || !File.Exists(java))
            Assert.Skip("Set UOTAN_APKSIG_JAR and UOTAN_JAVA (JDK 17+) for independent Android verification.");
        var root = Path.Combine(Path.GetTempPath(), "uotan-apksig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using AsymmetricAlgorithm key = kind.StartsWith("rsa", StringComparison.Ordinal)
                ? RSA.Create(kind == "rsa" ? 2048 : 4096)
                : ECDsa.Create(kind == "ec" ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384);
            var request = key is RSA rsa
                ? new CertificateRequest("CN=UotanStudio Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest("CN=UotanStudio Test", (ECDsa)key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            var keyPath = Path.Combine(root, "key.pem");
            var certPath = Path.Combine(root, "cert.pem");
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(certPath, cert.ExportCertificatePem());
            using var material = ApkSignerKeyMaterial.Load(keyPath, null, certPath, null);
            var output = Path.Combine(root, "signed.apk");
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "ApkSigning");
            var input = Path.Combine(fixture, "unsigned.apk");
            if (scheme == "all")
            {
                var native = Path.Combine(root, "native.apk");
                var generated = await RunJavaAsync(java!, jar!, fixture, "sign", input, native, keyPath, certPath,
                    kind.StartsWith("rsa", StringComparison.Ordinal) ? "RSA" : "EC");
                Assert.True(generated.ExitCode == 0, generated.Output);
                input = native;
            }
            ApkSigner.Sign(input, output, material, new ApkSigningOptions
            {
                EnableV1 = scheme is "v1" or "all",
                EnableV2 = scheme is "v2" or "all",
                EnableV3 = scheme is "v3" or "all"
            }, TestContext.Current.CancellationToken);
            // Platform SignedCms uses the ECDSA-with-SHA256 OID, supported by Android from API 21.
            var minimum = scheme == "v3" ? "28" : scheme == "all" ? "21" : "24";
            var valid = await VerifyAsync(java!, jar!, fixture, output, minimum);
            Assert.True(valid.ExitCode == 0, valid.Output);
            Assert.Contains("verified=true", valid.Output);
            var apkBytes = File.ReadAllBytes(output);
            var blockStart = scheme == "v1" ? apkBytes.Length : (int)FindBlock(output);
            // Flip compressed manifest content, leaving ZIP framing and signatures untouched.
            var nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(apkBytes.AsSpan(26));
            var extraLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(apkBytes.AsSpan(28));
            var position = 30 + nameLength + extraLength + 8;
            Assert.True(position < blockStart);
            apkBytes[position] ^= 1;
            File.WriteAllBytes(output, apkBytes);
            var invalid = await VerifyAsync(java!, jar!, fixture, output, minimum);
            Assert.NotEqual(0, invalid.ExitCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static long FindBlock(string path)
    {
        using var stream = File.OpenRead(path);
        return ApkSigningBlock.Find(stream, ApkZipSections.Locate(stream))!.Value.Offset;
    }

    private static async Task<(int ExitCode, string Output)> VerifyAsync(string java, string jar, string fixture, string apk, string min)
        => await RunJavaAsync(java, jar, fixture, apk, min, "35");

    private static async Task<(int ExitCode, string Output)> RunJavaAsync(string java, string jar, string fixture, params string[] arguments)
    {
        var start = new ProcessStartInfo(java) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--class-path", jar, Path.Combine(fixture, "Verify.java") }.Concat(arguments)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}