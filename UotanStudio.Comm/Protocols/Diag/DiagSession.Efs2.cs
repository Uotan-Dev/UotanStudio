using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Diag;

/// <summary>EFS2 file operations on a <see cref="DiagSession"/>: directory listing, stat, file read/write and deletion. The session performs no authorization: the caller must have unlocked the device itself.
/// <para><see cref="DiagSession"/> 上的 EFS2 文件操作：列目录、状态查询、文件读写与删除。会话不做授权：调用方必须自行完成设备解锁。</para></summary>
public sealed partial class DiagSession
{
    /// <summary>
    /// Lists one EFS directory.
    /// <para>列出一个 EFS 目录。</para>
    /// </summary>
    /// <param name="path">The directory path. <para>目录路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Entry names with their type flags. <para>条目名与类型标志。</para></returns>
    public async Task<IReadOnlyList<(string Name, bool IsDirectory)>> ListEfsDirectoryAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ValidateEfsPath(path);
        var body = JoinEfsParts([DiagSubsysPacket.EncodeEfsString(path), [0, 0, 0, 1]]);
        var reply = await SendEfs2Async(DiagConstants.Efs2ListDirectory, body, cancellationToken).ConfigureAwait(false);
        return ParseDirectoryEntries(reply);
    }

    /// <summary>
    /// Reads one EFS file. Large files are walked in chunks until the device reports EOF.
    /// <para>读取一个 EFS 文件。大文件按块循环读取，直到设备报告文件结束。</para>
    /// </summary>
    /// <param name="path">The file path. <para>文件路径。</para></param>
    /// <param name="maximumBytes">The accepted file size upper bound.
    /// <para>接受的文件大小上限。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The file content. <para>文件内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The file exceeds <paramref name="maximumBytes"/>.
    /// <para>文件超过 <paramref name="maximumBytes"/>。</para></exception>
    public async Task<byte[]> ReadEfsFileAsync(string path, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateEfsPath(path);
        var buffer = new MemoryStream();
        var sequence = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = new byte[4 + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0, 4), (uint)sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4, 4), DiagConstants.MaxEfsChunkBytes);
            var body = JoinEfsParts([head, DiagSubsysPacket.EncodeEfsString(path)]);
            var reply = await SendEfs2Async(DiagConstants.Efs2GetFile, body, cancellationToken).ConfigureAwait(false);
            if (reply.Length < 4)
            {
                throw new DiagException("The EFS2 get-file reply is shorter than its chunk header.");
            }

            var chunk = BinaryPrimitives.ReadUInt32LittleEndian(reply);
            if (chunk == 0)
            {
                break;
            }

            if (reply.Length < 4 + chunk)
            {
                throw new DiagException("The EFS2 get-file reply truncated its chunk.");
            }

            buffer.Write(reply.AsSpan(4, (int)chunk));
            if (buffer.Length > maximumBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes),
                    $"The EFS file '{path}' exceeds the accepted {maximumBytes} bytes.");
            }

            sequence++;
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Creates or truncates one EFS file with the given content. Writing device EFS state is a
    /// configuration change; callers must gate it themselves.
    /// <para>用给定内容创建或截断一个 EFS 文件。写入设备 EFS 状态属于配置变更；调用方必须自行把关。</para>
    /// </summary>
    /// <param name="path">The file path. <para>文件路径。</para></param>
    /// <param name="content">The file content. <para>文件内容。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the write.
    /// <para>传 <see langword="true"/> 确认写入。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">The confirmation was not given.
    /// <para>未给出确认。</para></exception>
    public async Task WriteEfsFileAsync(string path, ReadOnlyMemory<byte> content, bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException("Writing an EFS file requires explicit confirmation.", nameof(confirm));
        }

        ValidateEfsPath(path);
        var sequence = 0;
        var offset = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Math.Min(DiagConstants.MaxEfsChunkBytes, content.Length - offset);
            var head = new byte[4 + 4 + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0, 4), (uint)sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4, 4), (uint)chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8, 4), 0);
            var parts = new List<byte[]>(3) { head, DiagSubsysPacket.EncodeEfsString(path) };
            if (chunk > 0)
            {
                parts.Add(content.Slice(offset, chunk).ToArray());
            }

            await SendEfs2Async(DiagConstants.Efs2PutFile, JoinEfsParts(parts), cancellationToken).ConfigureAwait(false);
            offset += chunk;
            sequence++;
        }
        while (offset < content.Length);
    }

    /// <summary>
    /// Deletes one EFS file.
    /// <para>删除一个 EFS 文件。</para>
    /// </summary>
    /// <param name="path">The file path. <para>文件路径。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the deletion.
    /// <para>传 <see langword="true"/> 确认删除。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">The confirmation was not given.
    /// <para>未给出确认。</para></exception>
    public async Task DeleteEfsFileAsync(string path, bool confirm, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException("Deleting an EFS file requires explicit confirmation.", nameof(confirm));
        }

        ValidateEfsPath(path);
        await SendEfs2Async(DiagConstants.Efs2DeleteFile, DiagSubsysPacket.EncodeEfsString(path), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the EFS synchronization state word.
    /// <para>读取 EFS 同步状态字。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The raw sync status. <para>原始同步状态。</para></returns>
    public async Task<ushort> ReadEfsSyncStatusAsync(CancellationToken cancellationToken = default)
    {
        byte[] syncBody = [0, 0];
        var reply = await SendEfs2Async(DiagConstants.Efs2SyncStatus, syncBody, cancellationToken).ConfigureAwait(false);
        return reply.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(reply) : (ushort)0;
    }

    private static void ValidateEfsPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException($"The EFS path '{path}' must be absolute.", nameof(path));
        }
    }

    private static byte[] JoinEfsParts(IReadOnlyList<byte[]> parts)
    {
        var length = 0;
        foreach (var part in parts)
        {
            length += part.Length;
        }

        var joined = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(joined, offset);
            offset += part.Length;
        }

        return joined;
    }

    private static List<(string Name, bool IsDirectory)> ParseDirectoryEntries(byte[] reply)
    {
        var entries = new List<(string, bool)>();
        var offset = 4;
        while (offset + 4 <= reply.Length)
        {
            var recordSize = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(offset, 4));
            if (recordSize < 4 || offset + recordSize > reply.Length)
            {
                break;
            }

            var type = reply[offset + 4];
            var nameBytes = reply.AsSpan(offset + 6, (int)Math.Min(recordSize - 6, reply.Length - offset - 6));
            var nul = nameBytes.IndexOf((byte)0);
            if (nul >= 0)
            {
                nameBytes = nameBytes[..nul];
            }

            entries.Add((Encoding.UTF8.GetString(nameBytes), type == 1));
            offset += (int)recordSize;
        }

        return entries;
    }
}