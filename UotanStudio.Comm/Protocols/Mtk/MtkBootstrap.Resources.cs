namespace UotanStudio.Comm.Protocols.Mtk;

public static partial class MtkBootstrap
{
    private static MtkBootResourceKind[] CaptureBootResourceOrder(MtkBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.BootResourceOrder);
        if (options.BootResourceOrder.Count > 2)
        {
            throw new ArgumentException("At most one certificate and one authentication resource may be selected.", nameof(options));
        }
        var order = new MtkBootResourceKind[options.BootResourceOrder.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = options.BootResourceOrder[i];
        }
        if ((order.Length > 0 && options.BootResourceProvider is null)
            || order.Distinct().Count() != order.Length
            || order.Any(kind => kind is not (MtkBootResourceKind.RootCertificate or MtkBootResourceKind.Authentication)))
        {
            throw new ArgumentException("Boot resources require a provider and distinct, explicitly supported kinds.", nameof(options));
        }
        return order;
    }

    /// <summary>
    /// Selects the resources to upload when the caller supplied a provider without an explicit
    /// order: the target's own security bits decide, in the upstream order (authentication before
    /// root certificate). An explicit selection always wins, and a provider without a matching
    /// requirement uploads nothing.
    /// <para>当调用方提供了提供方但未给出显式顺序时选定要上传的资源：由目标自身的安全位决定，
    /// 顺序沿用上游约定（先认证资源后根证书）。显式选择始终优先；提供方存在但目标无相应要求时
    /// 不上传任何资源。</para>
    /// </summary>
    /// <param name="captured">The explicit order captured before device I/O. <para>设备 I/O 前捕获的显式顺序。</para></param>
    /// <param name="provider">The configured resource provider, or null. <para>已配置的资源提供方，或为 null。</para></param>
    /// <param name="device">The identity and security bits reported by the target. <para>目标上报的身份与安全位。</para></param>
    /// <returns>The kinds to upload, in order; empty when nothing is required. <para>待上传的资源种类（有序）；无要求时为空。</para></returns>
    internal static MtkBootResourceKind[] ResolveBootResourceOrder(
        MtkBootResourceKind[] captured, IMtkBootResourceProvider? provider, MtkDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(device);
        if (captured.Length > 0 || provider is null)
        {
            return captured;
        }

        var derived = new List<MtkBootResourceKind>(2);
        if (device.DownloadAgentAuthenticationRequired)
        {
            derived.Add(MtkBootResourceKind.Authentication);
        }

        if (device.RootCertificateRequired)
        {
            derived.Add(MtkBootResourceKind.RootCertificate);
        }

        return [.. derived];
    }

    private static async Task UploadBootResourcesAsync(MtkSession session, MtkDeviceInfo device,
        IMtkBootResourceProvider? provider, MtkBootResourceKind[] order, CancellationToken cancellationToken)
    {
        var resources = new List<MtkBootResource>(order.Length);
        Exception? failure = null;
        try
        {
            foreach (var kind in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resource = await provider!.OpenAsync(device, kind, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The boot resource provider returned no resource.");
                if (resources.Contains(resource))
                {
                    throw new InvalidOperationException("The provider must return distinct owned resources.");
                }
                resources.Add(resource);
                cancellationToken.ThrowIfCancellationRequested();
                MtkSession.ValidateBootResource(resource.Stream, resource.Length);
                if (resources.Take(resources.Count - 1).Any(previous => ReferenceEquals(previous.Stream, resource.Stream)))
                {
                    throw new InvalidOperationException("Boot resources cannot share the same mutable stream.");
                }
            }
            for (var i = 0; i < resources.Count; i++)
            {
                await session.UploadBootResourceByteStreamAsync(order[i], resources[i].Stream,
                    resources[i].Length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var resource in resources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            if (failures is not null)
            {
                if (failure is null)
                {
                    throw new AggregateException("Boot resource cleanup failed.", failures);
                }
                failure.Data["MtkBootResourceCleanupErrors"] = failures.ToArray();
            }
        }
    }
}