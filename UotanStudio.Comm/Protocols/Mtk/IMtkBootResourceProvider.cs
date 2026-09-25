namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Supplies caller-authorized resources for the explicit boot byte-stream profile.
/// <para>为显式启动字节流配置提供经调用方授权的资源。</para></summary>
public interface IMtkBootResourceProvider
{
    /// <summary>Opens a new caller-owned resource for a verified loader/device identity match.
    /// <para>针对已校验的 loader/设备身份匹配，打开一个新的由调用方持有的资源。</para></summary>
    /// <param name="device">Identity queried from the selected target; not proof of cryptographic identity.
    /// <para>从所选目标查询到的身份；并非密码学身份的证明。</para></param>
    /// <param name="kind">The resource explicitly selected by the caller, in caller-selected order.
    /// <para>由调用方显式选择的资源，按调用方选定的顺序。</para></param>
    /// <param name="cancellationToken">Cancellation for external resource acquisition.
    /// <para>用于外部资源获取的取消。</para></param>
    /// <returns>A new owned resource; bootstrap disposes it after upload or any later failure.
    /// <para>新持有的资源；引导流程在上传或此后任何失败后将其释放。</para></returns>
    Task<MtkBootResource> OpenAsync(MtkDeviceInfo device, MtkBootResourceKind kind,
        CancellationToken cancellationToken);
}

/// <summary>One owned resource stream and an explicit selected byte count; no resource bytes are copied.
/// <para>一个持有的资源流与一个显式选定的字节数；不复制任何资源字节。</para></summary>
public sealed class MtkBootResource : IDisposable
{
    private Stream? _owned;

    /// <summary>Takes ownership of a non-null stream; bootstrap validates its declared selection before upload.
    /// <para>获取一个非 null 流的所有权；引导流程在上传前校验其声明的选择。</para></summary>
    /// <param name="stream">New readable stream, exclusively owned by this lease.
    /// <para>新的可读流，由本租约独占持有。</para></param>
    /// <param name="length">Selected byte count, validated before any resource upload.
    /// <para>选定的字节数，在任何资源上传之前校验。</para></param>
    public MtkBootResource(Stream stream, int length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _owned = stream;
        Length = length;
    }

    /// <summary>Gets the owned stream, or throws after disposal.
    /// <para>获取所持有的流，若在释放后访问则抛出异常。</para></summary>
    /// <value>The owned readable stream.
    /// <para>所持有的可读流。</para></value>
    public Stream Stream => _owned ?? throw new ObjectDisposedException(nameof(MtkBootResource));

    /// <summary>Gets the selected byte count; bytes after this selection are not sent.
    /// <para>获取选定的字节数；此选择之后的字节不会被发送。</para></summary>
    /// <value>The number of selected bytes to upload.
    /// <para>要上传的选定字节数。</para></value>
    public int Length { get; }

    /// <summary>Releases the owned stream at most once.
    /// <para>至多释放所持有的流一次。</para></summary>
    public void Dispose() => Interlocked.Exchange(ref _owned, null)?.Dispose();
}