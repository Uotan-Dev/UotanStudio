namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Loads fastboot device profiles from a JSON manifest (see FastbootDeviceProfiles).
/// Uses a lightweight, AOT-safe parser so the library stays compatible with NativeAOT and
/// trimming (no reflection-based System.Text.Json).
/// <para>从 JSON 清单加载 fastboot 设备档案（参见 FastbootDeviceProfiles）。
/// 使用轻量、AOT 安全的解析器，保证库在 NativeAOT 与裁剪下兼容
/// （不依赖基于反射的 System.Text.Json）。</para>
/// </summary>
internal static class FastbootDeviceProfileLoader
{
    /// <summary>
    /// Loads device profiles from the specified JSON file. Entries without a vendor id or
    /// product id, malformed entries and missing files are skipped, so a partially broken
    /// manifest never breaks discovery.
    /// <para>从指定 JSON 文件加载设备档案。缺少厂商 ID 或产品 ID、格式错误的条目以及缺失的
    /// 文件会被跳过，因此部分损坏的清单不会破坏设备发现。</para>
    /// </summary>
    /// <param name="path">Path to the JSON manifest. JSON 清单路径。</param>
    /// <returns>The loaded profiles. 加载到的设备档案。</returns>
    public static List<FastbootDeviceProfile> LoadFromFile(string path)
    {
        var profiles = new List<FastbootDeviceProfile>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return profiles;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch
        {
            return profiles;
        }

        try
        {
            foreach (var entry in ParseObjects(FindArray(json, "profiles")))
            {
                try
                {
                    var name = GetStringValue(entry, "name");
                    var vendorText = GetStringValue(entry, "vendorId");
                    var productText = GetStringValue(entry, "productId");

                    if (!TryParseHex16(vendorText, out var vendorId) || vendorId == 0)
                    {
                        continue;
                    }

                    if (!TryParseHex16(productText, out var productId))
                    {
                        continue;
                    }

                    var profileName = string.IsNullOrWhiteSpace(name) ? $"VID 0x{vendorId:X4} PID 0x{productId:X4}" : name;
                    profiles.Add(new FastbootDeviceProfile(vendorId, productId, profileName));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return profiles;
    }

    /// <summary>
    /// Loads device profiles from a JSON manifest and returns them, or the built-in defaults
    /// when the manifest is missing, unreadable or contains no usable entries.
    /// <para>从 JSON 清单加载设备档案并返回；清单缺失、不可读或无可用条目时返回内置默认档案。</para>
    /// </summary>
    /// <param name="path">Path to the JSON manifest. JSON 清单路径。</param>
    /// <returns>The profiles to use. 要使用的设备档案。</returns>
    public static IReadOnlyList<FastbootDeviceProfile> LoadOrDefault(string path)
    {
        var loaded = LoadFromFile(path);
        return loaded.Count > 0 ? loaded : FastbootDeviceProfiles.Default;
    }

    private static bool TryParseHex16(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Locates the first '[' ... ']' array that belongs to the given member name.
    /// <para>定位属于指定成员名的第一个 '[' ... ']' 数组。</para>
    /// </summary>
    private static string FindArray(string json, string memberName)
    {
        var nameIndex = json.IndexOf("\"" + memberName + "\"", StringComparison.OrdinalIgnoreCase);
        if (nameIndex < 0)
        {
            return "";
        }

        var bracket = json.IndexOf('[', nameIndex);
        if (bracket < 0)
        {
            return "";
        }

        var depth = 0;
        var inString = false;
        for (var i = bracket; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\'))
            {
                inString = !inString;
            }

            if (inString)
            {
                continue;
            }

            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return json.Substring(bracket, i - bracket + 1);
                }
            }
        }
        return "";
    }

    /// <summary>
    /// Splits a '[...]' array into its top-level '{...}' object strings.
    /// <para>将 '[...]' 数组拆分为其顶层 '{...}' 对象字符串。</para>
    /// </summary>
    private static IEnumerable<string> ParseObjects(string arrayJson)
    {
        if (string.IsNullOrEmpty(arrayJson))
        {
            yield break;
        }

        var i = 0;
        while (i < arrayJson.Length)
        {
            var open = arrayJson.IndexOf('{', i);
            if (open < 0)
            {
                yield break;
            }

            var depth = 0;
            var inString = false;
            var close = -1;
            for (var j = open; j < arrayJson.Length; j++)
            {
                var c = arrayJson[j];
                if (c == '"' && (j == 0 || arrayJson[j - 1] != '\\'))
                {
                    inString = !inString;
                }

                if (inString)
                {
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = j;
                        break;
                    }
                }
            }

            if (close < 0)
            {
                yield break;
            }

            yield return arrayJson.Substring(open, close - open + 1);
            i = close + 1;
        }
    }

    /// <summary>
    /// Reads the string value of a member inside a single '{...}' object string.
    /// <para>在单个 '{...}' 对象字符串中读取指定成员名的字符串值。</para>
    /// </summary>
    private static string? GetStringValue(string objectJson, string memberName)
    {
        var nameIndex = objectJson.IndexOf("\"" + memberName + "\"", StringComparison.OrdinalIgnoreCase);
        if (nameIndex < 0)
        {
            return null;
        }

        var colon = objectJson.IndexOf(':', nameIndex);
        if (colon < 0)
        {
            return null;
        }

        var i = colon + 1;
        while (i < objectJson.Length && char.IsWhiteSpace(objectJson[i]))
        {
            i++;
        }

        if (i >= objectJson.Length || objectJson[i] != '"')
        {
            return null;
        }

        i++;
        var sb = new System.Text.StringBuilder();
        while (i < objectJson.Length)
        {
            var c = objectJson[i];
            if (c == '\\' && i + 1 < objectJson.Length)
            {
                sb.Append(objectJson[i + 1]);
                i += 2;
                continue;
            }
            if (c == '"')
            {
                return sb.ToString();
            }

            sb.Append(c);
            i++;
        }
        return null;
    }
}