namespace UotanStudio.Services.Compare;

/// <summary>
/// File operations and comparison data, independent of document state and display rows.
/// <para>文件操作与比对数据契约，与文档状态和展示行相互独立。</para>
/// </summary>
internal interface IComparisonService
{
    /// <summary>
    /// Probes text content while keeping registered firmware extensions in binary mode.
    /// <para>探测文本内容，已注册的固件扩展名始终按二进制处理。</para>
    /// </summary>
    /// <param name="path">File path to probe. <para>要探测的文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the probe. <para>观测探测过程的取消令牌。</para></param>
    /// <returns>True when the file is text-like. <para>文件形似文本时为 true。</para></returns>
    Task<bool> IsTextLikeFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a single directory in ordinal relative-path order.
    /// <para>按相对路径的序数顺序读取单个目录。</para>
    /// </summary>
    /// <param name="path">Directory to read. <para>要读取的目录。</para></param>
    /// <param name="cancellationToken">Token observing the read. <para>观测读取过程的取消令牌。</para></param>
    /// <returns>The ordered directory items. <para>排序后的目录条目。</para></returns>
    Task<IReadOnlyList<FolderCompareItem>> ReadDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads BOM-aware text, bounded to two million lines and 256 MiB.
    /// <para>读取带 BOM 探测的文本，上限为两百万行与 256 MiB。</para>
    /// </summary>
    /// <param name="path">Text file to read. <para>要读取的文本文件。</para></param>
    /// <param name="cancellationToken">Token observing the read. <para>观测读取过程的取消令牌。</para></param>
    /// <returns>The lines of the file. <para>文件的各行。</para></returns>
    Task<IReadOnlyList<string>> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes text lines back to a file: the original encoding (detected from the BOM) and line ending are preserved,
    /// and a temporary file next to the target is atomically swapped in.
    /// <para>把文本行写回文件：保留原文件的编码（按 BOM 判定）与换行风格，先写同目录临时文件再原子替换。</para>
    /// </summary>
    /// <param name="path">Target file path. <para>目标文件路径。</para></param>
    /// <param name="lines">Lines to write. <para>要写入的行。</para></param>
    /// <param name="cancellationToken">Token observing the write. <para>观测写入过程的取消令牌。</para></param>
    Task SaveTextAsync(string path, IReadOnlyList<string> lines, CancellationToken cancellationToken);

    /// <summary>
    /// Reads firmware metadata without extracting or scanning binary differences.
    /// <para>读取固件元数据，不解包也不扫描二进制差异。</para>
    /// </summary>
    /// <param name="path">File to inspect. <para>要检查的文件。</para></param>
    /// <param name="cancellationToken">Token observing the read. <para>观测读取过程的取消令牌。</para></param>
    /// <returns>The metadata read for the file. <para>该文件读到的元数据。</para></returns>
    Task<ComparisonMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Compares two text files using the existing bounded line-diff algorithm.
    /// <para>使用既有的有上限行级差异算法比较两个文本文件。</para>
    /// </summary>
    /// <param name="left">Left file path. <para>左文件路径。</para></param>
    /// <param name="right">Right file path. <para>右文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the comparison. <para>观测比对过程的取消令牌。</para></param>
    /// <returns>The aligned diff lines. <para>对齐后的差异行。</para></returns>
    Task<IReadOnlyList<TextDiffLine>> CompareTextAsync(string left, string right, CancellationToken cancellationToken);

    /// <summary>
    /// Compares two directories using the existing recursive comparison policy.
    /// <para>使用既有的递归比较策略比较两个目录。</para>
    /// </summary>
    /// <param name="left">Left directory path. <para>左目录路径。</para></param>
    /// <param name="right">Right directory path. <para>右目录路径。</para></param>
    /// <param name="cancellationToken">Token observing the comparison. <para>观测比对过程的取消令牌。</para></param>
    /// <returns>The folder comparison result. <para>文件夹比对结果。</para></returns>
    Task<FolderCompareResult> CompareDirectoriesAsync(string left, string right, CancellationToken cancellationToken);

    /// <summary>
    /// Scans binary differences between two files and fills the supplied row bitmap.
    /// <para>扫描两个文件之间的二进制差异，并写入传入的行位图。</para>
    /// </summary>
    /// <param name="left">Left file path. <para>左文件路径。</para></param>
    /// <param name="right">Right file path. <para>右文件路径。</para></param>
    /// <param name="rowBitmap">Row bitmap to fill. <para>要写入的行位图。</para></param>
    /// <param name="cancellationToken">Token observing the scan. <para>观测扫描过程的取消令牌。</para></param>
    /// <returns>The binary diff statistics. <para>二进制差异统计信息。</para></returns>
    Task<BinaryDiffStats> CompareBinaryAsync(string left, string right, byte[] rowBitmap, CancellationToken cancellationToken);

    /// <summary>
    /// Copies one selected relative file through the existing safe relative-path resolver.
    /// <para>经既有的安全相对路径解析器复制一个选中的相对文件。</para>
    /// </summary>
    /// <param name="source">Source directory path. <para>源目录路径。</para></param>
    /// <param name="target">Target directory path. <para>目标目录路径。</para></param>
    /// <param name="relativePath">Relative file path inside the source directory. <para>源目录内的相对文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the copy. <para>观测复制过程的取消令牌。</para></param>
    Task CopyAsync(string source, string target, string relativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one selected relative file through the existing safe relative-path resolver.
    /// <para>经既有的安全相对路径解析器删除一个选中的相对文件。</para>
    /// </summary>
    /// <param name="directory">Directory containing the file. <para>包含该文件的目录。</para></param>
    /// <param name="relativePath">Relative file path inside the directory. <para>目录内的相对文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the deletion. <para>观测删除过程的取消令牌。</para></param>
    Task DeleteAsync(string directory, string relativePath, CancellationToken cancellationToken);
}