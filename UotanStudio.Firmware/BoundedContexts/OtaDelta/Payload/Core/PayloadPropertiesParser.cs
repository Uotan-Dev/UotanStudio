namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal static class PayloadPropertiesParser
{
    /// <summary>
    /// Parses payload properties from raw text content.
    /// <para>从原始文本内容解析载荷属性。</para>
    /// </summary>
    internal static Models.PayloadProperties Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var eqIndex = line.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = line[..eqIndex].Trim();
                var value = line[(eqIndex + 1)..].Trim();
                properties[key] = value;
            }
        }

        var result = new Models.PayloadProperties
        {
            FileHash = properties.TryGetValue("FILE_HASH", out var fh) ? fh : string.Empty,
            MetadataHash = properties.TryGetValue("METADATA_HASH", out var mh) ? mh : string.Empty,
            Properties = properties
        };

        if (properties.TryGetValue("FILE_SIZE", out var fsStr) && long.TryParse(fsStr, out var fs))
        {
            result.FileSize = fs;
        }

        if (properties.TryGetValue("METADATA_SIZE", out var msStr) && long.TryParse(msStr, out var ms))
        {
            result.MetadataSize = ms;
        }

        return result;
    }

    /// <summary>
    /// Parses a payload_properties.txt file from disk.
    /// <para>从磁盘解析 payload_properties.txt 文件。</para>
    /// </summary>
    internal static Models.PayloadProperties ParseFile(string filepath)
    {
        return filepath is null
            ? throw new ArgumentNullException(nameof(filepath))
            : string.IsNullOrWhiteSpace(filepath)
            ? throw new ArgumentException("File path cannot be empty or whitespace.", nameof(filepath))
            : Parse(File.ReadAllText(filepath));
    }

    /// <summary>
    /// Parses payload_properties.txt from a ZIP archive.
    /// <para>从 ZIP 归档中解析 payload_properties.txt。</para>
    /// </summary>
    internal static Models.PayloadProperties ParseFromZip(string zipPath)
    {
        if (zipPath is null)
        {
            throw new ArgumentNullException(nameof(zipPath));
        }

        if (string.IsNullOrWhiteSpace(zipPath))
        {
            throw new ArgumentException("Zip path cannot be empty or whitespace.", nameof(zipPath));
        }

        const string propertiesEntryName = "payload_properties.txt";
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(propertiesEntryName)
            ?? throw new FileNotFoundException($"{propertiesEntryName} not found in the zip file");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}