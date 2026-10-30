using System.Reflection;

namespace UotanStudio.Assets;

internal sealed class EmbeddedAssetPayloadSource : IAssetPayloadSource
{
    private readonly Assembly _assembly;

    internal EmbeddedAssetPayloadSource(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembly = assembly;
    }

    public byte[] Read(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                $"The asset resource '{resourceName}' is missing from {_assembly.GetName().Name}. "
                + $"程序集 {_assembly.GetName().Name} 中缺少资源 '{resourceName}'。");
        if (stream.Length is <= 0 or > AssetLimits.MaximumEntryBytes)
        {
            throw new InvalidDataException(
                $"The asset resource '{resourceName}' declares {stream.Length} bytes, which is outside the accepted range. "
                + $"资源 '{resourceName}' 声明 {stream.Length} 字节，超出可接受范围。");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}