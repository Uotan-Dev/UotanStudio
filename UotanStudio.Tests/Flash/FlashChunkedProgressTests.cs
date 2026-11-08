using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// The chunked fastboot path over a scripted transport: an image larger than the negotiated download limit is
/// re-cut into sparse chunks, and every chunk reports its own numbered status instead of repeating the first one.
/// <para>脚本化传输上的 fastboot 分块路径：超过协商下载上限的镜像被重新切割为稀疏分片，每个分片上报自己的编号状态，
/// 而不是重复第一个分片的状态。</para>
/// </summary>
public sealed class FlashChunkedProgressTests
{
    [Fact]
    public void FlashImageChunked_ImageOverTheDownloadLimit_ReportsEveryChunkOnceInOrder()
    {
        var transport = new FastbootScriptedTransport(maxDownloadSize: 1024 * 1024);
        using var session = new FastbootSession(transport, timeoutMs: 2000);
        var image = Path.Combine(Path.GetTempPath(),
            "uotan-chunk-progress-" + Guid.NewGuid().ToString("N") + ".img");
        var reported = new List<FlashProgress>();
        try
        {
            WriteRawImage(image, (int)(2L * 1024 * 1024));

            var summary = FlashService.FlashImageChunked(session, "system_a", image,
                new SynchronousFlashProgressAdapter(reported), CancellationToken.None);

            // The cut chunks each report their own numbered status, and none of them repeats: gating the page log on
            // the percent alone printed chunk 1 per wire chunk and dropped the rest.
            // 每个切出的分片各自上报自己的编号状态，且没有任何一条重复：只按百分比门控页面日志会把第 1 块在每个线上
            // 分块上重复打印，并丢掉其余分块。
            var chunkTemplate = LanguageService.Instance.GetString("Flash_Progress_UploadingChunk");
            var chunkPrefix = chunkTemplate[..chunkTemplate.IndexOf("{0}", StringComparison.Ordinal)];
            var chunkStatuses = reported
                .Select(progress => progress.Status)
                .Where(status => status.Contains(chunkPrefix, StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(chunkStatuses);
            Assert.Equal(chunkStatuses.Length, chunkStatuses.Distinct(StringComparer.Ordinal).Count());
            var expectedStatuses = Enumerable.Range(1, chunkStatuses.Length).Select(index => string.Format(
                CultureInfo.CurrentCulture, chunkTemplate, index, chunkStatuses.Length)).ToArray();
            Assert.Equal(expectedStatuses, chunkStatuses);
            Assert.Contains("system_a", summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(image);
        }
    }

    private static void WriteRawImage(string path, int bytes)
    {
        var buffer = new byte[bytes];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)index;
        }

        File.WriteAllBytes(path, buffer);
    }

    /// <summary>
    /// A fastboot peer that answers <c>getvar:max-download-size</c>, accepts every <c>download:</c> payload and
    /// acknowledges the following <c>flash:</c>, so the chunk loop runs without a device.
    /// <para>应答 <c>getvar:max-download-size</c>、接受每个 <c>download:</c> 负载并确认其后的 <c>flash:</c> 的 fastboot 端，
    /// 使分块循环无需设备即可运行。</para>
    /// </summary>
    private sealed class FastbootScriptedTransport(int maxDownloadSize) : ICommTransport
    {
        private readonly Queue<byte[]> _replies = [];
        private long _dataExpected;

        public bool IsConnected => true;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }

        public long Write(byte[] data, int offset, int count, int timeoutMs)
        {
            if (_dataExpected > 0)
            {
                _dataExpected -= count;
                if (_dataExpected <= 0)
                {
                    _dataExpected = 0;
                    _replies.Enqueue("OKAY"u8.ToArray());
                }

                return count;
            }

            var command = Encoding.UTF8.GetString(data, offset, count);
            if (command.StartsWith("download:", StringComparison.Ordinal) &&
                long.TryParse(command["download:".Length..], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var size))
            {
                _dataExpected = size;
                _replies.Enqueue(Encoding.ASCII.GetBytes($"DATA{size:x8}"));
            }
            else if (command is "getvar:is-logical:system_a" or "getvar:is-userspace" or
                     "getvar:slot-count" or "getvar:current-slot")
            {
                _replies.Enqueue("OKAYno"u8.ToArray());
            }
            else if (command.StartsWith("getvar:", StringComparison.Ordinal))
            {
                _replies.Enqueue(Encoding.ASCII.GetBytes($"OKAY{maxDownloadSize}"));
            }
            else
            {
                _replies.Enqueue("OKAY"u8.ToArray());
            }

            return count;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_replies.Count == 0)
            {
                return 0;
            }

            var reply = _replies.Dequeue();
            var copied = Math.Min(count, reply.Length);
            reply.AsSpan(0, copied).CopyTo(buffer.AsSpan(offset));
            if (copied < reply.Length)
            {
                _replies.Enqueue(reply[copied..]);
            }

            return copied;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(buffer, offset, count, timeoutMs));

        public Task<long> WriteAsync(byte[] data, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Write(data, offset, count, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that appends every report to a list on the calling thread, so the order the
    /// flash produced is the order the assertions see.
    /// <para>在调用线程上把每次上报追加到列表的 <see cref="IProgress{T}"/>，使断言看到的顺序就是刷写产生的顺序。</para>
    /// </summary>
    private sealed class SynchronousFlashProgressAdapter(List<FlashProgress> reported) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => reported.Add(value);
    }
}