namespace UotanStudio.Services.Compare;

/// <summary>File operations and comparison data, independent of document state and display rows.</summary>
internal interface IComparisonService
{
    /// <summary>Probes text content while keeping registered firmware extensions in binary mode.</summary>
    Task<bool> IsTextLikeFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads a single directory in ordinal relative-path order.</summary>
    Task<IReadOnlyList<FolderCompareItem>> ReadDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads BOM-aware text, bounded to two million lines and 256 MiB.</summary>
    Task<IReadOnlyList<string>> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// <para>把文本行写回文件：保留原文件的编码（按 BOM 判定）与换行风格，先写同目录临时文件再原子替换。</para>
    /// Writes text lines back to a file: the original encoding (detected from the BOM) and line ending are preserved,
    /// and a temporary file next to the target is atomically swapped in.
    /// </summary>
    Task SaveTextAsync(string path, IReadOnlyList<string> lines, CancellationToken cancellationToken);

    /// <summary>Reads firmware metadata without extracting or scanning binary differences.</summary>
    Task<ComparisonMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken);

    /// <summary>Compares two text files using the existing bounded line-diff algorithm.</summary>
    Task<IReadOnlyList<TextDiffLine>> CompareTextAsync(string left, string right, CancellationToken cancellationToken);

    /// <summary>Compares two directories using the existing recursive comparison policy.</summary>
    Task<FolderCompareResult> CompareDirectoriesAsync(string left, string right, CancellationToken cancellationToken);

    /// <summary>Scans binary differences and fills the caller-owned row bitmap.</summary>
    Task<BinaryDiffStats> CompareBinaryAsync(string left, string right, byte[] rowBitmap, CancellationToken cancellationToken);

    /// <summary>Copies a selected relative file through the existing safe path resolver.</summary>
    Task CopyAsync(string source, string target, string relativePath, CancellationToken cancellationToken);

    /// <summary>Deletes a selected relative file through the existing safe path resolver.</summary>
    Task DeleteAsync(string directory, string relativePath, CancellationToken cancellationToken);
}