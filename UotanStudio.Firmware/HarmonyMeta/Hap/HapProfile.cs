using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text.Json;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Parsed provisioning profile (<c>.p7b</c>): the CMS wrapper is unwrapped to the embedded profile JSON and the
/// fields install-time verification relies on are exposed.
/// <para>已解析的 Profile（<c>.p7b</c>）：解开 CMS 包装得到内嵌 Profile JSON，并暴露装机校验所需的字段。</para>
/// </summary>
/// <remarks>
/// A profile is an attached CMS SignedData whose content is the profile JSON document. The parser reads the first
/// balanced JSON object from the content so malformed trailing bytes cannot change the accepted fields, and every
/// read is bounded by the caller-provided byte budget.
/// <para>Profile 是 attached CMS SignedData，其内容为 Profile JSON 文档。解析器从内容中读取首个配平的 JSON
/// 对象，尾部多余字节不影响接受字段，全部读取受调用方给定的字节预算约束。</para>
/// </remarks>
public sealed class HapProfile
{
    private const int MaximumProfileBytes = 4 << 20;

    private HapProfile(byte[] rawProfile, JsonElement json)
    {
        RawProfile = rawProfile;
        _json = json.Clone();
    }

    private readonly JsonElement _json;

    /// <summary>Gets the raw profile bytes as embedded in the block. <para>块内嵌的原始 Profile 字节。</para></summary>
    /// <value>Profile bytes, signed CMS or raw JSON. <para>Profile 字节，已签 CMS 或原始 JSON。</para></value>
    public byte[] RawProfile { get; }

    /// <summary>Gets the profile type, e.g. <c>debug</c>. <para>Profile 类型，例如 <c>debug</c>。</para></summary>
    public string Type => GetString("type");

    /// <summary>Gets the application bundle name the profile binds.
    /// <para>Profile 绑定的应用包名。</para></summary>
    public string BundleName => GetBundleString("bundle-name");

    /// <summary>Gets the developer (team) identifier. <para>开发者（团队）标识。</para></summary>
    public string DeveloperId => GetBundleString("developer-id");

    /// <summary>Gets the application identifier. <para>应用标识。</para></summary>
    public string AppIdentifier => GetBundleString("app-identifier");

    /// <summary>Gets the profile issuer, for example <c>app_gallery</c>. <para>Profile 签发方，例如 <c>app_gallery</c>。</para></summary>
    public string Issuer => GetString("issuer");

    /// <summary>Gets the profile validity window in Unix seconds.
    /// <para>Profile 有效期（Unix 秒）。</para></summary>
    public (long NotBefore, long NotAfter) Validity
    {
        get
        {
            if (!_json.TryGetProperty("validity", out var validity))
            {
                return (0, 0);
            }

            return (GetNumber(validity, "not-before"), GetNumber(validity, "not-after"));
        }
    }

    /// <summary>Gets the device UDIDs a debug profile authorizes. <para>debug Profile 授权的设备 UDID 列表。</para></summary>
    public IReadOnlyList<string> DeviceIds
    {
        get
        {
            if (!_json.TryGetProperty("debug-info", out var debug)
                || !debug.TryGetProperty("device-ids", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>(ids.GetArrayLength());
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind is JsonValueKind.String)
                {
                    result.Add(id.GetString() ?? string.Empty);
                }
            }

            return result;
        }
    }

    /// <summary>Gets the development certificate PEM embedded in the profile, or an empty string.
    /// <para>Profile 内嵌的开发证书 PEM，无则为空串。</para></summary>
    public string DevelopmentCertificatePem => GetBundleString("development-certificate");

    /// <summary>
    /// Parses a profile from raw bytes; the bytes may be a signed CMS (<c>.p7b</c>) or a raw JSON document.
    /// <para>从原始字节解析 Profile；字节可为已签 CMS（<c>.p7b</c>）或原始 JSON 文档。</para>
    /// </summary>
    /// <param name="bytes">Profile file bytes. <para>Profile 文件字节。</para></param>
    /// <returns>The parsed profile. <para>解析后的 Profile。</para></returns>
    /// <exception cref="InvalidDataException">The bytes exceed the size budget, are not a valid CMS or JSON
    /// document, or the embedded JSON object is missing.
    /// <para>字节超出大小预算、不是合法 CMS 或 JSON 文档，或内嵌 JSON 对象缺失。</para></exception>
    public static HapProfile Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumProfileBytes)
        {
            throw new InvalidDataException($"Profile size {bytes.Length} is out of range. Profile 大小 {bytes.Length} 越界。");
        }

        var content = ExtractContent(bytes.ToArray());
        var start = content.IndexOf((byte)'{');
        if (start < 0)
        {
            throw new InvalidDataException("Profile content carries no JSON object. Profile 内容不含 JSON 对象。");
        }

        var depth = 0;
        var end = -1;
        for (var index = start; index < content.Length; index++)
        {
            switch (content[index])
            {
                case (byte)'{':
                    depth++;
                    break;
                case (byte)'}':
                    depth--;
                    if (depth == 0)
                    {
                        end = index + 1;
                    }

                    break;
            }

            if (end >= 0)
            {
                break;
            }
        }

        if (end < 0)
        {
            throw new InvalidDataException("Profile JSON object is unbalanced. Profile JSON 对象不配平。");
        }

        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(content[start..end]);
            json = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Profile JSON is malformed: {exception.Message}. Profile JSON 非法：{exception.Message}。");
        }

        return new HapProfile(content, json);
    }

    /// <summary>
    /// Reports whether the profile authorizes the supplied device UDID; a profile without a device list authorizes
    /// every device.
    /// <para>报告 Profile 是否授权给定设备 UDID；无设备列表的 Profile 授权所有设备。</para>
    /// </summary>
    /// <param name="udid">Device UDID. <para>设备 UDID。</para></param>
    /// <returns><see langword="true"/> when authorized. <para>已授权时为 <see langword="true"/>。</para></returns>
    public bool AuthorizesDevice(string udid)
    {
        var ids = DeviceIds;
        return ids.Count == 0 || ids.Contains(udid, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] ExtractContent(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf("-----BEGIN"u8.ToArray()) >= 0
            || (bytes.Length > 1 && bytes[0] == (byte)'{'))
        {
            return bytes;
        }

        try
        {
            var cms = new SignedCms();
            cms.Decode(bytes);
            return cms.ContentInfo.Content;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"Profile is neither PEM, JSON nor a CMS document: {exception.Message}. " +
                $"Profile 既非 PEM、JSON 也非 CMS 文档：{exception.Message}。");
        }
    }

    private string GetString(string name)
        => _json.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private string GetBundleString(string name)
        => _json.TryGetProperty("bundle-info", out var bundle)
            && bundle.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

    private static long GetNumber(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : 0;
}