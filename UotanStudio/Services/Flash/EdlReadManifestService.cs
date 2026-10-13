using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Executes the <c>&lt;read&gt;</c> directives of a read command list against an initialized
/// Firehose session, publishing each verified range to its named file and aborting at the
/// first failure.
/// <para>在已初始化的 Firehose 会话上执行读清单的 <c>&lt;read&gt;</c> 指令，每段校验通过的
/// 范围发布到所指文件，首个失败即中止整张清单。</para>
/// </summary>
internal static class EdlReadManifestService
{
    /// <summary>
    /// Reads every entry of a read manifest into the output directory, in document order, stopping at the first
    /// failure.
    /// <para>把读清单的每个条目按文档顺序读入输出目录，并在第一个失败处停止。</para>
    /// </summary>
    /// <param name="firehose">
    /// The initialized Firehose protocol owned by the session. <para>会话持有的已初始化 Firehose 协议。</para>
    /// </param>
    /// <param name="manifestPath">
    /// Path of the read-type XML file. <para>read-type XML 文件路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory that receives one file per entry. <para>按条目接收文件的目录。</para>
    /// </param>
    /// <param name="progress">
    /// Optional per-entry progress. <para>可选的逐条目进度。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation observed before every entry and inside every transfer. <para>在每个条目之前以及每次传输内部
    /// 都被观察的取消令牌。</para>
    /// </param>
    /// <returns>
    /// One result per successfully published entry, in document order. <para>按文档顺序为每个成功发布的条目返回
    /// 一条结果。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="firehose"/> is null. <para><paramref name="firehose"/> 为 null。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The manifest path or output directory is null, empty or white space. <para>清单路径或输出目录为 null、空
    /// 或仅含空白。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A read entry is missing, names a file outside the output directory, or contradicts the
    /// reported geometry. <para>读条目缺失、指向输出目录之外，或与上报几何矛盾。</para>
    /// </exception>
    /// <exception cref="EdlException">
    /// The target rejected or short-read a range. <para>目标拒绝或短读了某段范围。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested before or between entries. <para>在条目之前或条目之间请求了取消。</para>
    /// </exception>
    internal static async Task<IReadOnlyList<EdlReadManifestResult>> ReadManifestAsync(FirehoseProtocol firehose,
        string manifestPath, string outputDirectory, IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var entries = ReadManifestParser.Parse(manifestPath);
        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                "The read manifest carries no <read> entry, so there is nothing to read back. 读清单不含 <read> " +
                "条目，没有可回读的内容。");
        }

        Directory.CreateDirectory(outputDirectory);
        var published = new List<EdlReadManifestResult>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;
            var destination = ResolveDestination(outputDirectory, entry.Filename, displayName);
            var geometry = await firehose.GetStorageGeometryAsync(entry.PhysicalPartitionNumber, cancellationToken)
                .ConfigureAwait(false);
            if (entry.SectorSizeInBytes > 0 && entry.SectorSizeInBytes != geometry.SectorSize)
            {
                throw new InvalidDataException(
                    $"The read entry '{displayName}' declares {entry.SectorSizeInBytes}-byte sectors but LUN " +
                    $"{entry.PhysicalPartitionNumber} reports {geometry.SectorSize}; the range is refused rather " +
                    $"than reinterpreted. 读条目 '{displayName}' 声明 {entry.SectorSizeInBytes} 字节扇区，而 LUN " +
                    $"{entry.PhysicalPartitionNumber} 上报 {geometry.SectorSize}，因此拒绝该范围而不做重新解释。");
            }

            var startSector = entry.ResolveStartSector(geometry);
            var temporaryPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
            string sha256Hex;
            long bytesTransferred;
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None))
                {
                    var read = await firehose.ReadPartitionToStreamAsync(output, startSector,
                        entry.NumPartitionSectors, entry.Filename, entry.PhysicalPartitionNumber,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    sha256Hex = read.Sha256Hex;
                    bytesTransferred = read.BytesWritten;
                }

                File.Move(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            published.Add(new EdlReadManifestResult(displayName, entry.PhysicalPartitionNumber, startSector,
                entry.NumPartitionSectors, bytesTransferred, sha256Hex, destination));
            progress?.Report(new FlashProgress(displayName,
                (int)Math.Min(99L, (index + 1L) * 100L / entries.Count),
                $"Read {bytesTransferred} bytes of '{displayName}' into {destination}. 已把 '{displayName}' 的 " +
                $"{bytesTransferred} 字节回读到 {destination}。"));
        }

        progress?.Report(new FlashProgress("read-manifest", 100,
            $"Published {published.Count} read-manifest files. 已发布 {published.Count} 个读清单文件。"));
        return published;
    }

    /// <summary>
    /// Turns a manifest filename into a path inside the output directory, refusing anything that could write
    /// outside it.
    /// <para>把清单文件名转换为输出目录内的路径，拒绝任何可能写到目录之外的取值。</para>
    /// </summary>
    /// <param name="outputDirectory">
    /// The chosen output directory. <para>选定的输出目录。</para>
    /// </param>
    /// <param name="filename">
    /// The declared filename, which must be a single path component. <para>声明的文件名，必须是单个路径成分。</para>
    /// </param>
    /// <param name="displayName">
    /// Label or filename used in the refusal. <para>拒绝信息里使用的标签或文件名。</para>
    /// </param>
    /// <returns>
    /// The absolute destination path. <para>绝对目标路径。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The name is empty, rooted, or carries a directory separator. <para>名称为空、带根，或含目录分隔符。</para>
    /// </exception>
    private static string ResolveDestination(string outputDirectory, string filename, string displayName)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new InvalidDataException(
                $"The read entry '{displayName}' declares no output filename, so the range cannot be published. " +
                $"读条目 '{displayName}' 未声明输出文件名，因此无法发布该范围。");
        }

        if (Path.IsPathRooted(filename) || filename.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || filename.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || filename == "." || filename == "..")
        {
            throw new InvalidDataException(
                $"The read entry '{displayName}' declares the output name '{filename}', which is not a single path " +
                "component inside the chosen directory; a manifest is untrusted input and cannot select where its " +
                $"files are written. 读条目 '{displayName}' 声明的输出名 '{filename}' 不是所选目录内的单个路径成分；" +
                "清单属于不可信输入，不能决定文件写到哪里。");
        }

        return Path.Combine(outputDirectory, filename);
    }
}

/// <summary>
/// One published entry of a read command list.
/// <para>读指令清单中一个已发布的条目。</para>
/// </summary>
/// <param name="Name">
/// Label, or filename when the manifest declares no label. <para>标签；清单未声明标签时为文件名。</para>
/// </param>
/// <param name="PhysicalPartitionNumber">
/// The LUN the range was read from. <para>本范围所读取的 LUN。</para>
/// </param>
/// <param name="StartSector">
/// The absolute start sector resolved against the reported geometry. <para>按上报几何解析出的绝对起始扇区。</para>
/// </param>
/// <param name="SectorCount">
/// The declared sector count. <para>声明的扇区数。</para>
/// </param>
/// <param name="BytesTransferred">
/// The measured byte count. <para>实测的字节数。</para>
/// </param>
/// <param name="Sha256Hex">
/// Lowercase hex SHA-256 of the transferred bytes. <para>传输字节的十六进制小写 SHA-256。</para>
/// </param>
/// <param name="DestinationPath">
/// The published file path. <para>已发布的文件路径。</para>
/// </param>
internal sealed record EdlReadManifestResult(string Name, int PhysicalPartitionNumber, long StartSector,
    long SectorCount, long BytesTransferred, string Sha256Hex, string DestinationPath);