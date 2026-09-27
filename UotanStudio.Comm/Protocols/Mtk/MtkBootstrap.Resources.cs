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
        if ((order.Length == 0) != (options.BootResourceProvider is null)
            || order.Distinct().Count() != order.Length
            || order.Any(kind => kind is not (MtkBootResourceKind.RootCertificate or MtkBootResourceKind.Authentication)))
        {
            throw new ArgumentException("Boot resources require a provider and distinct, explicitly supported kinds.", nameof(options));
        }
        return order;
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