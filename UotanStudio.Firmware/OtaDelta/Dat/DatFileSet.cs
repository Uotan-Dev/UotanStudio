namespace UotanStudio.Firmware.OtaDelta.Dat;

/// <summary>
/// Single source of truth for Android block-OTA DAT/BR file-set naming. One partition contributes a
/// group that shares a common stem — <c>&lt;stem&gt;.transfer.list</c>, the full-image
/// <c>&lt;stem&gt;.new.dat</c> (optionally Brotli-compressed to <c>&lt;stem&gt;.new.dat.br</c>), and the
/// optional incremental <c>&lt;stem&gt;.patch.dat</c> (<c>.patch.dat.br</c>). Every layer that must
/// recognise, group or strip these suffixes (the firmware <c>DatFormatHandler</c>, the delivery
/// <c>Package</c> builder and the host explorer) calls into these helpers instead of re-declaring the
/// suffix list.
/// <para>Android 块 OTA DAT/BR 文件集命名的唯一权威来源。同一分区共享同一词干构成一组：
/// <c>&lt;词干&gt;.transfer.list</c>、全量 <c>&lt;词干&gt;.new.dat</c>（可 Brotli 压缩为
/// <c>&lt;词干&gt;.new.dat.br</c>）以及可选增量 <c>&lt;词干&gt;.patch.dat</c>（<c>.patch.dat.br</c>）。
/// 凡需识别、分组或剥离这些后缀的层（固件 <c>DatFormatHandler</c>、交付 <c>Package</c> 构建器、
/// 宿主资源管理器）都调用本类，而不是各自复制后缀列表。</para>
/// </summary>
public static class DatFileSet
{
    /// <summary>The transfer-list suffix. <para>transfer list 后缀。</para></summary>
    public const string TransferListSuffix = ".transfer.list";

    /// <summary>Full-image data suffixes, longest first so <c>.new.dat.br</c> matches before <c>.new.dat</c>.
    /// <para>全量数据后缀，长后缀在前，确保 <c>.new.dat.br</c> 先于 <c>.new.dat</c> 命中。</para></summary>
    public static readonly string[] NewDataSuffixes = [".new.dat.br", ".new.dat"];

    /// <summary>Incremental patch data suffixes, longest first. <para>增量补丁数据后缀，长后缀在前。</para></summary>
    public static readonly string[] PatchDataSuffixes = [".patch.dat.br", ".patch.dat"];

    /// <summary>All data-file suffixes (new and patch), longest first. <para>全部数据文件后缀（新镜像与补丁），长后缀在前。</para></summary>
    public static readonly string[] DataSuffixes = [".new.dat.br", ".new.dat", ".patch.dat.br", ".patch.dat"];

    /// <summary>Every artifact suffix ordered for stem stripping. <para>用于剥离词干的完整后缀序列。</para></summary>
    public static readonly string[] OrderedSuffixes = [TransferListSuffix, ".new.dat.br", ".new.dat", ".patch.dat.br", ".patch.dat"];

    /// <summary>Determines whether a file name is a transfer list. <para>判断文件名是否为 transfer list。</para></summary>
    /// <param name="fileName">File name or path tested case-insensitively against the suffix.
    /// <para>按不区分大小写方式对后缀进行匹配的文件名或路径。</para></param>
    /// <returns><see langword="true"/> when the name ends with <c>.transfer.list</c>.
    /// <para>名称以 <c>.transfer.list</c> 结尾时为 <see langword="true"/>。</para></returns>
    public static bool IsTransferList(string fileName) => EndsWithAny(fileName, TransferListSuffix);

    /// <summary>Determines whether a file name is a full-image data file. <para>判断文件名是否为全量数据文件。</para></summary>
    /// <param name="fileName">File name or path tested case-insensitively against the suffixes.
    /// <para>按不区分大小写方式对后缀进行匹配的文件名或路径。</para></param>
    /// <returns><see langword="true"/> for <c>.new.dat</c> and <c>.new.dat.br</c> names.
    /// <para>名称以 <c>.new.dat</c> 或 <c>.new.dat.br</c> 结尾时为 <see langword="true"/>。</para></returns>
    public static bool IsNewData(string fileName) => EndsWithAny(fileName, NewDataSuffixes);

    /// <summary>Determines whether a file name is an incremental patch data file. <para>判断文件名是否为增量补丁数据文件。</para></summary>
    /// <param name="fileName">File name or path tested case-insensitively against the suffixes.
    /// <para>按不区分大小写方式对后缀进行匹配的文件名或路径。</para></param>
    /// <returns><see langword="true"/> for <c>.patch.dat</c> and <c>.patch.dat.br</c> names.
    /// <para>名称以 <c>.patch.dat</c> 或 <c>.patch.dat.br</c> 结尾时为 <see langword="true"/>。</para></returns>
    public static bool IsPatchData(string fileName) => EndsWithAny(fileName, PatchDataSuffixes);

    /// <summary>Determines whether a file name is any DAT data file (new or patch). <para>判断文件名是否为任意 DAT 数据文件（新镜像或补丁）。</para></summary>
    /// <param name="fileName">File name or path tested case-insensitively against the suffixes.
    /// <para>按不区分大小写方式对后缀进行匹配的文件名或路径。</para></param>
    /// <returns><see langword="true"/> when the name matches <see cref="IsNewData"/> or <see cref="IsPatchData"/>.
    /// <para>名称命中 <see cref="IsNewData"/> 或 <see cref="IsPatchData"/> 时为 <see langword="true"/>。</para></returns>
    public static bool IsDataFile(string fileName) => IsNewData(fileName) || IsPatchData(fileName);

    /// <summary>Determines whether a file name is a DAT/BR flashing artifact (transfer list or data).
    /// <para>判断文件名是否为 DAT/BR 刷写工件（transfer list 或数据文件）。</para></summary>
    /// <param name="fileName">File name or path tested case-insensitively against the suffixes.
    /// <para>按不区分大小写方式对后缀进行匹配的文件名或路径。</para></param>
    /// <returns><see langword="true"/> for transfer-list and all data-file names.
    /// <para>transfer list 及全部数据文件名均为 <see langword="true"/>。</para></returns>
    public static bool IsArtifact(string fileName) => IsTransferList(fileName) || IsDataFile(fileName);

    /// <summary>Determines whether a data file is Brotli-compressed. <para>判断数据文件是否为 Brotli 压缩。</para></summary>
    /// <param name="fileName">File name or path tested case-insensitively for a <c>.br</c> ending.
    /// <para>按不区分大小写方式检查是否以 <c>.br</c> 结尾的文件名或路径。</para></param>
    /// <returns><see langword="true"/> when the name ends with <c>.br</c>, regardless of the data role.
    /// <para>名称以 <c>.br</c> 结尾时为 <see langword="true"/>，不区分数据角色。</para></returns>
    public static bool IsBrotli(string fileName) => EndsWithAny(fileName, ".br");

    /// <summary>Strips the DAT artifact suffix returning the partition stem; non-artifacts return the name unchanged.
    /// <para>剥离 DAT 工件后缀返回分区词干；非工件原样返回文件名。</para></summary>
    /// <param name="fileName">File name or path whose recognized artifact suffix is removed.
    /// <para>需要剥离已识别工件后缀的文件名或路径。</para></param>
    /// <returns>The name without its artifact suffix, matched longest-first; the input unchanged for non-artifacts.
    /// <para>按长后缀优先剥离工件后缀后的名称；非工件时原样返回输入。</para></returns>
    public static string Stem(string fileName)
    {
        foreach (var suffix in OrderedSuffixes)
        {
            if (EndsWithAny(fileName, suffix))
            {
                return fileName[..^suffix.Length];
            }
        }

        return fileName;
    }

    private static bool EndsWithAny(string fileName, string suffix) =>
        fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithAny(string fileName, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}