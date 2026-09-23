using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>An explicitly selected bootloader security resource, not a credential source or bypass.</summary>
public enum MtkBootResourceKind
{
    /// <summary>A vendor-issued root certificate sent with boot command E0.</summary>
    RootCertificate,
    /// <summary>A vendor-issued authentication resource sent with boot command E2.</summary>
    Authentication
}

public sealed partial class MtkSession
{
    /// <summary>
    /// Uploads an explicitly authorized security resource using the COM byte-stream wire
    /// profile. A successful exchange leaves the bootloader stage unchanged and does not
    /// prove SLA completion or permission to execute a particular download agent.
    /// </summary>
    /// <remarks>
    /// The caller must select the byte-stream profile, not a USB implementation requiring
    /// packet-size-dependent writes or explicit zero-length packets. No resource is fetched,
    /// padded, patched or retried. Strict checksum matching is required, including zero.
    /// Default bootstrap and agent upload do not invoke this method. Bootstrap invokes it
    /// only for explicitly selected kinds with a configured byte-stream resource provider.
    /// </remarks>
    /// <param name="kind">Certificate or authentication resource command.</param>
    /// <param name="resource">Readable caller-owned resource stream at the selected bytes.</param>
    /// <param name="length">Positive even byte count, at most 64 MiB; trailing bytes are not sent.</param>
    /// <param name="cancellationToken">Cancellation for waiting, reading and communication.</param>
    public async Task UploadBootResourceByteStreamAsync(MtkBootResourceKind kind, Stream resource, int length,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (MtkBootResourceKind.RootCertificate or MtkBootResourceKind.Authentication))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ValidateBootResource(resource, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Bootloader);
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new byte[1024];
            try
            {
                await EchoAsync([kind == MtkBootResourceKind.RootCertificate ? (byte)0xE0 : (byte)0xE2],
                    cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async((uint)length, cancellationToken).ConfigureAwait(false);
                var initial = BinaryPrimitives.ReadUInt16BigEndian(
                    await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
                if (initial > 0xFF || (kind == MtkBootResourceKind.Authentication && initial == 0xFF))
                {
                    throw new MtkProtocolException("The bootloader rejected the security resource request.", initial);
                }
                ushort checksum = 0;
                var remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, remaining);
                    await resource.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    for (var offset = 0; offset < count; offset += 2)
                    {
                        checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
                    }
                    await WriteAsync(buffer, count, _timeoutMs, cancellationToken).ConfigureAwait(false);
                    remaining -= count;
                }
                var response = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
                CheckStatus(BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)), "security resource upload");
                if (BinaryPrimitives.ReadUInt16BigEndian(response) != checksum)
                {
                    throw new MtkProtocolException("The security resource checksum did not match.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch
            {
                _state = MtkSessionState.Faulted;
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    internal static void ValidateBootResource(Stream resource, int length)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumAgentLength);
        if ((length & 1) != 0 || !resource.CanRead
            || (resource.CanSeek && resource.Length - resource.Position < length))
        {
            throw new ArgumentException("The resource must contain the complete, even-length selection.", nameof(resource));
        }
    }
}