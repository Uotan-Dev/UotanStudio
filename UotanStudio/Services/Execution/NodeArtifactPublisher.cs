using System.Text;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Execution;

/// <summary>Publishes one node artifact through a same-directory temporary file without buffering an input file.<para>通过同目录临时文件发布单个节点产物，不缓冲输入文件。</para></summary>
internal static class NodeArtifactPublisher
{
    /// <summary>Copies supported input data, preserving the previous destination if copying or cancellation fails.<para>复制受支持的输入数据，复制或取消失败时保留原有目标。</para></summary>
    /// <param name="data">Supported input data to publish.<para>待发布的受支持输入数据。</para></param>
    /// <param name="path">Destination file path.<para>目标文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The published byte length.<para>已发布的字节长度。</para></returns>
    internal static async Task<long> PublishAsync(object? data, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            long length;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                switch (data)
                {
                    case byte[] bytes:
                        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        break;
                    case ReadOnlyMemory<byte> memory:
                        await output.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                        break;
                    case Stream source:
                        await source.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                        break;
                    case FileArtifact artifact:
                        await using (var source = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            await source.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case string text:
                        await using (var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true))
                        {
                            await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    default:
                        throw new ArgumentException("Artifact input must be bytes, text, a file artifact or a readable stream.", nameof(data));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                length = output.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return length;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}