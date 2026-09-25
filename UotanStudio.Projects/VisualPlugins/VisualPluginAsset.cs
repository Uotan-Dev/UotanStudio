using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Identity of one portable patch resource; source paths are local authoring state only.
/// <para>单个可移植补丁资源的身份信息；源路径仅是本地编辑期间的状态。</para>
/// </summary>
public sealed class VisualPluginAsset
{
    /// <summary>
    /// Stable portable resource identifier.
    /// <para>稳定的可移植资源标识符。</para>
    /// </summary>
    /// <value>
    /// Manifest string matched ordinally and case-sensitively when a resource is looked up.
    /// <para>清单中的字符串；查找资源时按逐字符、区分大小写的方式匹配。</para>
    /// </value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display filename, not an extraction destination.
    /// <para>显示用文件名，不是解包目标路径。</para>
    /// </summary>
    /// <value>
    /// File name captured from the source path; it never carries directory information and is not used to place bytes on disk.
    /// <para>从源路径捕获的文件名；不携带目录信息，也不用于决定字节落盘的位置。</para>
    /// </value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Exact uncompressed length.
    /// <para>精确的未压缩长度。</para>
    /// </summary>
    /// <value>
    /// Byte count recorded when the identity was captured; every copy must produce exactly this many bytes and no more.
    /// <para>捕获身份时记录的字节数；每次复制必须恰好产生这么多字节且不能更多。</para>
    /// </value>
    public long Length { get; set; }

    /// <summary>
    /// SHA-256 identity in hexadecimal.
    /// <para>十六进制表示的 SHA-256 身份。</para>
    /// </summary>
    /// <value>
    /// Uppercase hex digest of the resource content; comparisons during copies ignore case, and bundle entry names use its lowercase form.
    /// <para>资源内容的大写十六进制摘要；复制时的比较不区分大小写，包内条目名使用其小写形式。</para>
    /// </value>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Trusted local authoring source; ignored during serialization and deserialization.
    /// <para>受信任的本地编辑源；序列化和反序列化时被忽略。</para>
    /// </summary>
    /// <value>
    /// Full path resolved at identity-capture time, or <see langword="null"/> when the resource lives only inside a bundle and reads go through <c>BundlePath</c> instead.
    /// <para>捕获身份时解析出的完整路径；当资源仅存在于包内、读取需经由 <c>BundlePath</c> 时为 <see langword="null"/>。</para>
    /// </value>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    internal VisualPluginAsset Snapshot() => new()
    {
        Id = Id,
        Name = Name,
        Length = Length,
        Sha256 = Sha256,
        SourcePath = SourcePath
    };

    /// <summary>
    /// Captures a selected file's identity without loading its contents into memory.
    /// <para>捕获所选文件的身份信息，而不把其内容整体读入内存。</para>
    /// </summary>
    /// <param name="id">
    /// Portable resource identifier stored on the returned asset as-is.
    /// <para>存入返回资源的可移植资源标识符，原样保存。</para>
    /// </param>
    /// <param name="path">
    /// File to hash; it is opened read-shared, streamed through SHA-256 in bounded chunks, and closed before returning.
    /// <para>待计算哈希的文件；以共享读方式打开，用有界分块经 SHA-256 流式处理，返回前即关闭。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the incremental hash; cancellation propagates and no asset is returned.
    /// <para>取消增量哈希；取消会向上传播且不会返回资源。</para>
    /// </param>
    /// <returns>
    /// Asset carrying the file's display name, exact length, uppercase-hex SHA-256 and full source path.
    /// <para>包含文件显示名、精确长度、大写十六进制 SHA-256 和完整源路径的资源。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid characters.
    /// <para><paramref name="path"/> 为 null、空或包含无效字符。</para></exception>
    /// <exception cref="InvalidDataException">
    /// The file exceeds <see cref="VisualPluginBundle.MaximumAssetSize"/>, shrank mid-read, or grew beyond its recorded length.
    /// <para>文件超过 <see cref="VisualPluginBundle.MaximumAssetSize"/>、读取中途变短或超出记录的长度。</para>
    /// </exception>
    /// <exception cref="IOException">The file cannot be opened. <para>文件无法打开。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task<VisualPluginAsset> FromFileAsync(string id, string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > VisualPluginBundle.MaximumAssetSize)
        {
            throw new InvalidDataException("Patch resource exceeds the per-resource size limit.");
        }
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = length;
        var buffer = new byte[131072];
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Patch resource changed while capturing its identity.");
            }
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Patch resource changed while capturing its identity.");
        }
        return new VisualPluginAsset
        {
            Id = id,
            Name = Path.GetFileName(path),
            Length = length,
            Sha256 = Convert.ToHexString(hash.GetHashAndReset()),
            SourcePath = Path.GetFullPath(path)
        };
    }
}