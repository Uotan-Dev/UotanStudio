using UotanStudio.Assets;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Delivery.Build;

/// <summary>
/// Materializes the bundled arm64 Edify updater: the delivery asset is decoded and checked against the manifest length
/// and SHA-256, then published atomically beside the destination.
/// <para>把随包的 arm64 Edify 更新器落盘：解码 delivery 资源并按清单记录的长度与 SHA-256 校验，再原子替换目标文件。</para>
/// </summary>
internal static class UpdaterAssets
{
    /// <summary>
    /// Returns the path of the verified native updater, decoding the bundle asset on first use.
    /// <para>返回已校验的原生更新器路径，首次使用时解码资源包中的负载。</para>
    /// </summary>
    /// <returns>Absolute path of the executable updater in the tool cache.
    /// <para>工具缓存中可执行更新器的绝对路径。</para></returns>
    /// <exception cref="KeyNotFoundException">The delivery bundle does not ship the updater file.
    /// <para>delivery 资源包未随包该更新器文件。</para></exception>
    /// <exception cref="InvalidDataException">The asset frame is missing, corrupt, or does not decode to the manifest
    /// length and digest. <para>资源帧缺失、损坏，或解码后与清单的长度、摘要不符。</para></exception>
    internal static string Extract()
    {
        var destination = Path.Combine(Path.GetTempPath(), "UotanStudio", "delivery-updater", "update-binary-arm64");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination) || !Matches(destination))
        {
            Publish(LoadVerified(), destination);
        }

        DeliveryUnixPermissions.SetExecutable(destination);
        return destination;
    }

    private static bool Matches(string path)
    {
        var entry = AssetCatalog.DeliveryUpdater[AssetNames.Delivery.Updater];
        using var source = File.OpenRead(path);
        return source.Length == entry.Length
            && string.Equals(Convert.ToHexStringLower(Sha256.ComputeHash(source)), entry.Sha256, StringComparison.Ordinal);
    }

    private static void Publish(byte[] bytes, string destination)
    {
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static byte[] LoadVerified() => AssetStore.Read(AssetCatalog.DeliveryUpdater, AssetNames.Delivery.Updater);
}