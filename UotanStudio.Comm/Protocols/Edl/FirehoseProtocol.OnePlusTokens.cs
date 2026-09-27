using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    private async Task<(string? PublicKey, string? Token)> GetOnePlusCommandTokenOwnedAsync(string command,
        CancellationToken cancellationToken)
    {
        if (_onePlusProject is not { } profile)
        {
            return (null, null);
        }
        var provider = profile.CommandTokenProvider
            ?? throw new InvalidOperationException("This OnePlus profile requires a per-command token provider for program and patch.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(profile.CommandTokenTimeoutMs);
        var token = deadline.Token;
        IReadOnlyDictionary<string, byte[]>? credentials = null;
        try
        {
            credentials = await provider.CreateCommandTokenAsync(profile.Kind, profile.ProjectId,
                profile.SerialNumber, _onePlusDeviceTimestamp, command, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var attributes = ValidateProjectCredentials(profile.Kind, credentials);
            return (attributes[0].Value, attributes[1].Value);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _faulted = true;
            throw new TimeoutException("The OnePlus command-token deadline expired; reconnect before continuing.", error);
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            if (credentials is not null)
            {
                foreach (var bytes in credentials.Values)
                {
                    if (bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
            }
        }
    }
}