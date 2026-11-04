namespace UotanStudio.Firmware.Executable.Qualcomm;

/// <summary>
/// The programmer image shapes a Sahara upload accepts: a single ELF, a standalone MBN, or a multi-ELF (MELF)
/// container of one or more components.
/// <para>Sahara 上传可接受的 programmer 镜像形态：单 ELF、独立 MBN，或由一个以上组件构成的多 ELF（MELF）容器。</para>
/// </summary>
public enum QualcommProgrammerKind
{
    /// <summary>Recognized structure was not found. <para>未找到可识别的结构。</para></summary>
    Unknown,
    /// <summary>A single ELF at offset zero. <para>偏移零处的单 ELF。</para></summary>
    Elf,
    /// <summary>A standalone MBN container. <para>独立的 MBN 容器。</para></summary>
    Mbn,
    /// <summary>A multi-ELF container, including scattered layouts whose components do not start at offset zero.
    /// <para>多 ELF 容器，包括组件不从偏移零处开始的零散布局。</para></summary>
    Melf
}

/// <summary>
/// What one candidate programmer file looks like: its shape and how many ELF components the bounded scan found.
/// <para>一个候选 programmer 文件的外观：其形态以及有界扫描找到的 ELF 组件数量。</para>
/// </summary>
/// <param name="Kind">The detected shape. <para>检测到的形态。</para></param>
/// <param name="ComponentCount">ELF components found; zero for a standalone MBN.
/// <para>找到的 ELF 组件数；独立 MBN 为零。</para></param>
public sealed record QualcommProgrammerShape(QualcommProgrammerKind Kind, int ComponentCount);

/// <summary>
/// Recognizes programmer image structure offline and without loading payloads into memory.
/// <para>离线识别 programmer 镜像结构，不把载荷读入内存。</para>
/// </summary>
/// <remarks>
/// The result states which known layout the bytes follow, never that a signature or an OEM identity was verified;
/// a caller that needs identity evidence still hashes the file and matches the descriptor.
/// <para>结果只说明字节遵循哪种已知布局，从不表示签名或 OEM 身份已验证；需要身份证据的调用方仍需计算哈希并匹配描述符。</para>
/// </remarks>
public static class QualcommProgrammerProbe
{
    /// <summary>Detects the programmer shape of one readable seekable stream and restores its position.
    /// <para>检测一个可读可寻址流的 programmer 形态并恢复其位置。</para>
    /// </summary>
    /// <param name="stream">Readable seekable candidate; its position is restored before returning.
    /// <para>可读可寻址的候选；返回前恢复其位置。</para></param>
    /// <param name="cancellationToken">Cancels the bounded component scan.
    /// <para>取消有界的组件扫描。</para></param>
    /// <returns>The detected shape and component count; <see cref="QualcommProgrammerKind.Unknown"/> when the bytes
    /// follow none of the supported layouts.
    /// <para>检测到的形态与组件数；字节不遵循任何受支持布局时为 <see cref="QualcommProgrammerKind.Unknown"/>。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or seeked.
    /// <para>流不可读或不可寻址。</para></exception>
    public static QualcommProgrammerShape Detect(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("A readable, seekable stream is required.", nameof(stream));
        }

        var position = stream.Position;
        try
        {
            if (stream.Length < 8)
            {
                return new(QualcommProgrammerKind.Unknown, 0);
            }

            var components = ScanComponents(stream, cancellationToken);
            if (HasElfMagicAtOrigin(stream))
            {
                return new(components > 1 ? QualcommProgrammerKind.Melf : QualcommProgrammerKind.Elf,
                    Math.Max(components, 1));
            }

            if (TryReadMbn(stream, cancellationToken))
            {
                return new(QualcommProgrammerKind.Mbn, 0);
            }

            return components > 0
                ? new(QualcommProgrammerKind.Melf, components)
                : new(QualcommProgrammerKind.Unknown, 0);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static bool HasElfMagicAtOrigin(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        stream.Position = 0;
        return stream.Read(magic) == magic.Length && magic.SequenceEqual("\u007fELF"u8);
    }

    /// <remarks>
    /// One component is a single ELF and only a second one proves a container; the probe still needs the
    /// count of one to tell a scattered layout, whose first ELF is not at offset zero, from an unknown file.
    /// <para>组件数为一是单 ELF，只有第二个组件才证明这是容器；探针仍需要知道一的存在，
    /// 才能把"首个 ELF 不在偏移零处"的零散布局与真正的未知文件区分开。</para>
    /// </remarks>
    private static int ScanComponents(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return QualcommMelfComponents.Scan(stream, cancellationToken, minimumComponentCount: 1).Count;
        }
        catch (Exception exception) when (exception is ExecutableException or InvalidDataException)
        {
            return 0;
        }
    }

    private static bool TryReadMbn(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            _ = QualcommMbnLayout.Read(stream, 0, stream.Length, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is ExecutableException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}