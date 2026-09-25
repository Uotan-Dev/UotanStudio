using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>One explicit loader-to-storage startup path without loader fallback, patching or authorization bypass.</summary>
public static partial class MtkXFlashBootstrap
{
    /// <summary>
    /// Takes ownership of a selected open transport, validates resources and initializes stock XFlash.
    /// The session is returned only after storage discovery; all failures dispose the owned transport.
    /// Null argument checks occur before ownership transfer.
    /// </summary>
    /// <param name="transport">The open transport selected by physical identity or explicit COM/TTY name.</param>
    /// <param name="options">Explicit resource selections and identity policy.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A caller-owned storage-ready session; this workflow issues no storage write or erase command.</returns>
    public static Task<MtkSession> InitializeAsync(ICommTransport transport, MtkXFlashBootstrapOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        return InitializeCoreAsync(transport, options, null, cancellationToken);
    }

    /// <summary>Preflights and retains the selected loader before opening one explicit serial port, then starts XFlash.</summary>
    /// <param name="serial">Explicit port and line settings; no probing or fallback is performed.</param>
    /// <param name="options">Explicit loader, stages, identity and storage selections.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>A caller-owned storage-ready session. Starting agents is not inherently side-effect-free.</returns>
    public static Task<MtkSession> ConnectSerialAsync(SerialConnectionOptions serial, MtkXFlashBootstrapOptions options,
        CancellationToken cancellationToken = default) =>
        ConnectSerialCoreAsync(serial, options, static async (settings, token) =>
            await settings.ConnectAsync(token).ConfigureAwait(false), cancellationToken);

    internal static Task<MtkSession> ConnectSerialCoreAsync(SerialConnectionOptions serial, MtkXFlashBootstrapOptions options,
        Func<SerialConnectionOptions, CancellationToken, Task<ICommTransport>> connect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connect);
        serial.Validate();
        return InitializeCoreAsync(null, options, token => connect(serial, token), cancellationToken);
    }

    private static async Task<MtkSession> InitializeCoreAsync(ICommTransport? transport, MtkXFlashBootstrapOptions options,
        Func<CancellationToken, Task<ICommTransport>>? connect, CancellationToken cancellationToken)
    {
        MtkSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourceOrder = CaptureBootResourceOrder(options);
            MtkSession.ValidateSlaTimeout(options.SlaTimeoutMs);
            if (!Enum.IsDefined(options.IdentityMatch) || !Enum.IsDefined(options.StorageKind))
            {
                throw new ArgumentException("The startup identity/storage policy is not supported.", nameof(options));
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TransferTimeoutMs);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.HandshakeTimeoutMs);
            using var loader = MtkDaFile.Open(options.LoaderPath, options.DirectoryLayout, cancellationToken);
            // V6 all-in-one containers are supported: the region directory drives the upload and
            // the BROM/XFlash wire protocol is DA-version independent (mtkclient uploads V6 DAs
            // through the same 0xD7 flow). The V6 marker stays available via HeaderIndicatesV6.
            if (options.StageOneRegionIndex == options.StageTwoRegionIndex)
            {
                throw new ArgumentException("DA1 and DA2 must be explicitly selected distinct regions.", nameof(options));
            }
            using var stageOne = loader.OpenRegion(options.EntryIndex, options.StageOneRegionIndex, MtkDaRegionContent.Complete);
            using var stageTwo = loader.OpenRegion(options.EntryIndex, options.StageTwoRegionIndex, options.StageTwoContent);
            var entry = loader.Metadata.Entries[options.EntryIndex];
            var first = entry.Regions[options.StageOneRegionIndex];
            var second = entry.Regions[options.StageTwoRegionIndex];
            ValidateRegion(first.LoadAddress, stageOne.Length, wordLength: true);
            ValidateRegion(second.LoadAddress, stageTwo.Length, wordLength: false);
            if (entry.HardwareCode == 0 || entry.SoftwareVersion is null)
            {
                throw new InvalidDataException("The selected entry lacks an explicit hardware/software identity.");
            }
            if (options.EmiParameters is { Length: <= 0 })
            {
                throw new InvalidDataException("The selected EMI payload is empty.");
            }

            if (transport is null)
            {
                transport = await connect!(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The serial connector returned no transport.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!transport.IsConnected)
            {
                throw new InvalidOperationException("The selected transport is not connected.");
            }
            session = new MtkSession(transport, options.TransferTimeoutMs, options.HandshakeTimeoutMs);
            var device = await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            ValidateIdentity(entry, device, options.IdentityMatch);
            await UploadBootResourcesAsync(session, device, options.BootResourceProvider, resourceOrder,
                cancellationToken).ConfigureAwait(false);
            if (options.BootRomSlaResponder is { } responder)
            {
                await session.UploadDownloadAgentWithSlaAsync(stageOne, first.LoadAddress, checked((int)stageOne.Length),
                    responder, checked((int)first.SignatureLength), options.SlaTimeoutMs,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.UploadDownloadAgentAsync(stageOne, first.LoadAddress, checked((int)stageOne.Length),
                    checked((int)first.SignatureLength), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            await session.StartDownloadAgentAsync(cancellationToken).ConfigureAwait(false);
            await session.InitializeXFlashAsync(options.DaLogLevel, cancellationToken).ConfigureAwait(false);
            var connection = await session.GetXFlashConnectionAgentAsync(cancellationToken).ConfigureAwait(false);
            if (connection == MtkXFlashConnectionAgent.BootRom && options.EmiParameters is null)
            {
                throw new InvalidOperationException("The Boot ROM path requires explicitly selected EMI parameters before DA2.");
            }
            if (options.EmiParameters is { } emi)
            {
                using var parameters = emi.OpenRead();
                await session.InitializeXFlashExternalRamAsync(parameters, emi.Length, cancellationToken).ConfigureAwait(false);
            }
            await session.UploadXFlashStageTwoAsync(stageTwo, second.LoadAddress, checked((int)stageTwo.Length),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (await session.DiscoverXFlashStorageAsync(options.StorageKind, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("The selected storage family was not reported by the agent.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            if (session is null)
            {
                transport?.Dispose();
            }
            else
            {
                session.Dispose();
            }
            throw;
        }
    }

    private static void ValidateRegion(uint address, long length, bool wordLength)
    {
        if (length <= 0 || length > 64 * 1024 * 1024 || (address & 3) != 0
            || (wordLength && ((length & 1) != 0 || (ulong)address + (ulong)length > (ulong)uint.MaxValue + 1)))
        {
            throw new InvalidDataException("A selected DA region is empty, oversized, unaligned or outside the supported load range.");
        }
    }

    private static void ValidateIdentity(MtkDaEntry entry, MtkDeviceInfo device, MtkDaIdentityMatch policy)
    {
        var revisionsMatch = policy == MtkDaIdentityMatch.Exact
            ? entry.HardwareVersion == device.HardwareVersion && entry.SoftwareVersion == device.SoftwareVersion
            : entry.HardwareVersion <= device.HardwareVersion && entry.SoftwareVersion <= device.SoftwareVersion;
        if (entry.HardwareCode != device.HardwareCode || entry.HardwareSubCode != device.HardwareSubCode || !revisionsMatch)
        {
            throw new InvalidOperationException("The selected loader entry does not match the queried hardware identity and revision policy.");
        }
    }
}