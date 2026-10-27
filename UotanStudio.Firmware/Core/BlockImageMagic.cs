namespace UotanStudio.Firmware.Core;

/// <summary>
/// Structural magic values shared by every block-image reader, writer and verifier, so a format change cannot drift
/// between the sparse container, the file systems it carries, the vendor handlers and the host services.
/// <para>供全部块镜像读取器、写入器与校验器共用的结构魔数，使格式变更不会在稀疏容器、其承载的文件系统、厂商处理器与
/// 宿主服务之间漂移。</para>
/// </summary>
public static class BlockImageMagic
{
    /// <summary>Little-endian magic value that identifies an Android sparse image file header.
    /// <para>标识 Android 稀疏镜像文件头部的小端魔数。</para></summary>
    public const uint SparseHeaderMagic = 0xED26FF3A;
}