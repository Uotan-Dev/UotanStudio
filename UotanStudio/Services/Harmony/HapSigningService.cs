using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.HarmonyMeta.Hap;
using UotanStudio.Hash.Digests;
using UotanStudio.Net.Agc;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Network;

namespace UotanStudio.Services.Harmony;

/// <summary>Signed-package outcome of one automatic signing run.
/// <para>一次自动签名产出包的结果。</para></summary>
/// <param name="PackagePath">Package ready for installation. <para>可用于安装的包路径。</para></param>
/// <param name="SignedNow">Whether the package was produced by this run.
/// <para>包是否由本次运行签出。</para></param>
/// <param name="BundleName">Application bundle name. <para>应用包名。</para></param>
public sealed record HapSigningOutcome(string PackagePath, bool SignedNow, string BundleName);

/// <summary>
/// Orchestrates DevEco-style automatic signing for a HAP package: the offline material checks run first, then the
/// AGC flow provisions whatever is missing (login, team, device registration, keystore, certificate, profile), and
/// finally the package is signed locally and handed back for installation.
/// <para>编排 DevEco 风格的 HAP 自动签名：先跑离线材料检查，再经 AGC 流程补齐缺口（登录、团队、设备注册、
/// keystore、证书、Profile），最后本地签名并交回安装。</para>
/// </summary>
/// <remarks>
/// Materials live under <c>&lt;data root&gt;/harmony/hap-signing/&lt;bundle&gt;/</c>. The private key is generated
/// locally and only its CSR travels to AGC; the certificate chain and profile come back as files. Repeated runs with
/// valid materials never touch the network.
/// <para>材料位于 <c>&lt;数据根&gt;/harmony/hap-signing/&lt;包名&gt;/</c>。私钥本地生成、只有 CSR 上送 AGC；
/// 证书链与 Profile 以文件形式返回。材料有效时重复运行不会访问网络。</para>
/// </remarks>
internal sealed class HapSigningService
{
    private static readonly Regex UdidPattern = new("[0-9A-Fa-f]{32,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan UdidTimeout = TimeSpan.FromSeconds(10);

    private readonly AgcSessionService _sessions = new();

    /// <summary>
    /// Ensures the package is signed and returns the path ready for installation.
    /// <para>确保包已签名并返回可安装路径。</para></summary>
    /// <param name="packagePath">Unsigned or signed HAP path. <para>未签或已签的 HAP 路径。</para></param>
    /// <param name="device">Target device used for UDID lookup. <para>用于 UDID 查询的目标设备。</para></param>
    /// <param name="progress">Step progress reporter. <para>步骤进度上报。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The signing outcome. <para>签名结果。</para></returns>
    /// <exception cref="InvalidOperationException">The bundle name cannot be read, or an AGC step fails.
    /// <para>无法读取包名，或 AGC 某一步失败。</para></exception>
    public async Task<HapSigningOutcome> EnsureSignedAsync(string packagePath, DetectedDevice device,
        IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (HapInspector.IsSigned(packagePath))
        {
            return new HapSigningOutcome(packagePath, SignedNow: false, BundleName: string.Empty);
        }

        var bundleName = await HapInspector.GetBundleNameAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (bundleName.Length == 0)
        {
            throw new InvalidOperationException("The HAP module metadata declares no bundleName; enter it manually. " +
                "HAP 模块元数据未声明 bundleName；请手动填写。");
        }

        var materialDirectory = Path.Combine(AppPaths.Root, "harmony", "hap-signing", bundleName);
        var keystorePath = Path.Combine(materialDirectory, "debug.p12");
        var certificatePath = Path.Combine(materialDirectory, "debug.cer");
        var profilePath = Path.Combine(materialDirectory, "debug.p7b");

        progress?.Report("Checking cached signing materials  检查缓存的签名材料");
        if (File.Exists(keystorePath) && File.Exists(certificatePath) && File.Exists(profilePath)
            && OfflineMaterialsValid(profilePath, bundleName))
        {
            return await SignAsync(packagePath, materialDirectory, bundleName, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var client = SharedHttpClient.Shared;
        var session = await _sessions.GetValidSessionAsync(client, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("Resolving the developer team  解析开发者团队");
        var teams = await AgcClient.GetTeamsAsync(client, new AgcCallContext(session.AccessToken, session.UserId),
            cancellationToken).ConfigureAwait(false);
        var team = teams.Count > 0 ? teams[0] : throw new InvalidOperationException(
            "The account has no developer team; create one on the AGC console first. 账号没有开发者团队；请先在 AGC 控制台创建。");
        var context = new AgcCallContext(session.AccessToken, session.UserId, team.TeamId);

        progress?.Report("Reading the device UDID  读取设备 UDID");
        var udid = await ReadUdidAsync(device, cancellationToken).ConfigureAwait(false);

        progress?.Report("Registering the device on AGC  在 AGC 注册设备");
        var deviceType = await ReadDeviceTypeAsync(device, cancellationToken).ConfigureAwait(false);
        var deviceIds = await EnsureDeviceAsync(client, context, device, udid, deviceType, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report("Preparing the local keystore  准备本地 keystore");
        if (!File.Exists(keystorePath))
        {
            var password = HapPassword.Generate();
            var generated = await FirmwareService.Instance.GenerateHapSigningKeyAsync(
                new HapSigningKeyOptions(KeyPassword: password), materialDirectory, cancellationToken).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(materialDirectory, "debug.pwd"), password);
            if (!string.Equals(generated.KeystorePath, keystorePath, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(generated.KeystorePath, keystorePath, overwrite: true);
                File.Move(generated.CsrPath, Path.Combine(materialDirectory, "debug.csr"), overwrite: true);
            }
        }

        var csrPath = Path.Combine(materialDirectory, "debug.csr");
        var certName = $"auto_debug_{team.TeamId}.cer";

        progress?.Report("Applying for the debug certificate  申请调试证书");
        var certId = await EnsureCertificateAsync(client, context, csrPath, certName, certificatePath, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report("Applying for the debug profile  申请调试 Profile");
        var provisionName = BuildProvisionName(bundleName);
        var provisionResponse = await AgcClient.AddDebugProvisionAsync(client, context,
            [certId], bundleName, provisionName, deviceIds, cancellationToken).ConfigureAwait(false);
        var profileUrl = FindFileUrl(provisionResponse.RawBody)
            ?? throw new InvalidOperationException("The profile response carries no file URL. Profile 响应不含文件地址。");
        var profileBytes = await DownloadMaterialAsync(client, context, profileUrl, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(materialDirectory);
        WriteAtomic(profilePath, profileBytes);

        return await SignAsync(packagePath, materialDirectory, bundleName, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether the cached materials still authorize the bundle and device.
    /// <para>报告缓存材料是否仍对包名与设备有效。</para></summary>
    /// <param name="profilePath">Cached profile path. <para>缓存 Profile 路径。</para></param>
    /// <param name="bundleName">Expected bundle name. <para>期望包名。</para></param>
    /// <returns><see langword="true"/> when the profile parses and matches.
    /// <para>Profile 可解析且匹配时为 <see langword="true"/>。</para></returns>
    private static bool OfflineMaterialsValid(string profilePath, string bundleName)
    {
        try
        {
            var profile = HapProfile.Parse(File.ReadAllBytes(profilePath));
            return string.Equals(profile.BundleName, bundleName, StringComparison.Ordinal)
                && profile.Validity.NotAfter > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private async Task<HapSigningOutcome> SignAsync(string packagePath, string materialDirectory,
        string bundleName, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Signing the package locally  本地签名");
        var output = Path.Combine(
            Path.GetDirectoryName(packagePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(packagePath) + "-signed.hap");
        var result = await FirmwareService.Instance.SignHapAsync(
            packagePath,
            output,
            Path.Combine(materialDirectory, "debug.p12"),
            ReadPassword(materialDirectory),
            "debugKey",
            Path.Combine(materialDirectory, "debug.cer"),
            Path.Combine(materialDirectory, "debug.p7b"),
            compatibleSdkVersion: 9,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }

        return new HapSigningOutcome(output, SignedNow: true, bundleName);
    }

    private static string ReadPassword(string materialDirectory)
    {
        // The keystore and its password file are always created together; a missing file means the material set is
        // incomplete and signing would produce an unopenable keystore error anyway.
        var passwordPath = Path.Combine(materialDirectory, "debug.pwd");
        if (!File.Exists(passwordPath))
        {
            throw new InvalidOperationException(
                "The keystore password file is missing; delete the material directory and retry. keystore 口令文件缺失；请删除材料目录后重试。");
        }

        return File.ReadAllText(passwordPath).Trim();
    }

    private static string BuildProvisionName(string bundleName)
    {
        var seed = Convert.ToHexStringLower(Sha256.ComputeHash(Encoding.UTF8.GetBytes($"uotan_{bundleName}_{bundleName}")));
        return seed[..16];
    }

    private static async Task<string> ReadUdidAsync(DetectedDevice device, CancellationToken cancellationToken)
    {
        var output = await HdcDeviceService.FromSettings().ShellAsync(device, "bm get --udid", cancellationToken)
            .ConfigureAwait(false);
        var matches = UdidPattern.Matches(output);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"The device did not report a UDID: {output.Trim()}. 设备未上报 UDID：{output.Trim()}。");
        }

        return matches.Select(match => match.Value).MaxBy(value => value.Length)!;
    }

    private static async Task<string> ReadDeviceTypeAsync(DetectedDevice device, CancellationToken cancellationToken)
    {
        try
        {
            var output = await HdcDeviceService.FromSettings()
                .ShellAsync(device, "getprop hw_sc.build.os.deviceType", cancellationToken).ConfigureAwait(false);
            return output.Trim().Length > 0 ? output.Trim() : "4";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return "4";
        }
    }

    private static async Task<IReadOnlyList<string>> EnsureDeviceAsync(HttpClient client, AgcCallContext context,
        DetectedDevice device, string udid, string deviceType, CancellationToken cancellationToken)
    {
        var known = new List<AgcDevice>();
        for (var start = 0; ; start += 100)
        {
            var page = await AgcClient.ListDevicesAsync(client, context, start, 100, cancellationToken)
                .ConfigureAwait(false);
            known.AddRange(page);
            if (page.Count < 100)
            {
                break;
            }
        }

        if (known.All(item => !string.Equals(item.Udid, udid, StringComparison.OrdinalIgnoreCase)))
        {
            await AgcClient.AddDeviceAsync(client, context, $"UotanStudio-{device.Serial}", udid, deviceType,
                cancellationToken).ConfigureAwait(false);
            // Re-list so the profile carries the complete cloud device id list.
            known = [.. (await AgcClient.ListDevicesAsync(client, context, 0, 100, cancellationToken).ConfigureAwait(false))];
        }

        return [.. known.Select(item => item.DeviceId)];
    }

    private static async Task<string> EnsureCertificateAsync(HttpClient client, AgcCallContext context,
        string csrPath, string certName, string certificatePath, CancellationToken cancellationToken)
    {
        var certificates = await AgcClient.ListCertificatesAsync(client, context, cancellationToken).ConfigureAwait(false);
        var existing = certificates.FirstOrDefault(item => string.Equals(item.CertName, certName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && File.Exists(certificatePath))
        {
            return existing.CertId;
        }

        var response = await AgcClient.AddCertificateAsync(client, context,
            File.ReadAllText(csrPath), certName, "debug", cancellationToken).ConfigureAwait(false);
        var certId = AgcClient.FindStringValue(response.RawBody, "certId");
        if (certId is not null)
        {
            // A freshly issued certificate may already carry its download URL in the apply response.
            var url = FindFileUrl(response.RawBody);
            if (url is not null)
            {
                var bytes = await DownloadMaterialAsync(client, context, url, cancellationToken).ConfigureAwait(false);
                WriteAtomic(certificatePath, bytes);
                return certId;
            }
        }

        // The certificate exists but the response carried no URL: find it in the list and fetch via reapply.
        certificates = await AgcClient.ListCertificatesAsync(client, context, cancellationToken).ConfigureAwait(false);
        var certificate = certificates.FirstOrDefault(item => string.Equals(item.CertName, certName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The issued certificate is not listed on AGC. 已签发证书未出现在 AGC 列表中。");
        var objectUrl = FindFileUrl(response.RawBody)
            ?? $"agc://cert/{certificate.CertId}";
        var downloadUrl = await AgcClient.ReapplyUrlAsync(client, context, objectUrl, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AGC returned no signed download URL for the certificate. AGC 未返回证书的签名下载地址。");
        var material = await DownloadMaterialAsync(client, context, downloadUrl, cancellationToken).ConfigureAwait(false);
        WriteAtomic(certificatePath, material);
        return certificate.CertId;
    }

    private static string? FindFileUrl(ReadOnlySpan<byte> body)
        => AgcClient.FindStringValue(body, "certFileUrl")
            ?? AgcClient.FindStringValue(body, "provisionFileUrl")
            ?? AgcClient.FindStringValue(body, "fileUrl")
            ?? AgcClient.FindStringValue(body, "downloadUrl");

    private static async Task<byte[]> DownloadMaterialAsync(HttpClient client, AgcCallContext context, string url,
        CancellationToken cancellationToken)
    {
        if (url.StartsWith("agc://", StringComparison.Ordinal))
        {
            var signed = await AgcClient.ReapplyUrlAsync(client, context, url, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("AGC returned no signed download URL for the material. AGC 未返回材料的签名下载地址。");
            return await AgcClient.DownloadAsync(client, signed, 16 << 20, cancellationToken).ConfigureAwait(false);
        }

        return await AgcClient.DownloadAsync(client, url, 16 << 20, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = path + ".tmp";
        File.WriteAllBytes(staged, bytes);
        File.Move(staged, path, overwrite: true);
    }
}

/// <summary>Generates the keystore password file content for the automatic flow.
/// <para>为自动流程生成 keystore 口令文件内容。</para></summary>
internal static class HapPassword
{
    /// <summary>Generates a random alphanumeric password.
    /// <para>生成随机字母数字口令。</para></summary>
    /// <returns>The password. <para>口令。</para></returns>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var text = Convert.ToBase64String(bytes);
        return new string([.. text.Where(char.IsAsciiLetterOrDigit)]);
    }
}