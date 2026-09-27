using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    private EdlProjectAuthenticationOptions? _onePlusProject;
    private long? _onePlusDeviceTimestamp;

    /// <summary>
    /// Completes one OnePlus or Nothing project authentication exchange without credential or project fallback.
    /// <para>完成一次 OnePlus 或 Nothing 项目认证交换，不执行凭据或项目回退。</para>
    /// </summary>
    /// <param name="options">The explicit device, project, variant and provider.
    /// <para>显式的设备、项目、变体和提供方。</para></param>
    /// <param name="timeoutMs">The cooperative deadline, from 1 ms through 24 hours.
    /// <para>范围为 1 毫秒至 24 小时的协作式截止时间。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the exchange.
    /// <para>等待与交换的取消令牌。</para></param>
    /// <returns>The final verified response; does not imply that per-program tokens are unnecessary.
    /// <para>最终经校验的响应，不表示后续 program 命令无需独立令牌。</para></returns>
    /// <exception cref="FirehoseException">Credentials or device verification fields are invalid.
    /// <para>凭据或设备校验字段无效。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired.
    /// <para>协作式截止时间已到。</para></exception>
    public async Task<FirehoseResponse> AuthenticateProjectAsync(EdlProjectAuthenticationOptions options,
        int timeoutMs = 60000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ValidateAuthenticationTimeout(timeoutMs);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await AuthenticateProjectOwnedAsync(options, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FirehoseResponse> AuthenticateProjectOwnedAsync(EdlProjectAuthenticationOptions options,
        int timeoutMs, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        IReadOnlyDictionary<string, byte[]>? credentials = null;
        try
        {
            long? timestamp = null;
            if (options.Kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject)
            {
                var fields = new AuthenticationFields();
                var start = await SendCommandAndWaitResponseAsync(BuildAuthenticationCommand("setprocstart"), token,
                    blockObserver: fields.Observe).ConfigureAwait(false);
                RequireAuthenticationAck(start);
                if (!long.TryParse(fields.GetRequired("setprocstart", "device_timestamp", "device_timestamp"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
                {
                    throw new FirehoseException("The OnePlus device timestamp is invalid.");
                }
                timestamp = value;
            }
            else if (options.Kind == EdlProjectAuthenticationKind.NothingProject)
            {
                var feature = await SendCommandAndWaitResponseAsync(BuildAuthenticationCommand("checkntfeature"), token).ConfigureAwait(false);
                RequireAuthenticationAck(feature);
            }

            credentials = await options.Provider.CreateCredentialsAsync(options.Kind, options.ProjectId,
                options.SerialNumber, timestamp, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var command = options.Kind switch
            {
                EdlProjectAuthenticationKind.OnePlusDemacia => "demacia",
                EdlProjectAuthenticationKind.OnePlusProject => "setprojmodel",
                EdlProjectAuthenticationKind.OnePlusSoftwareProject => "setswprojmodel",
                EdlProjectAuthenticationKind.NothingProject => "ntprojectverify",
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            };
            var attributes = ValidateProjectCredentials(options.Kind, credentials);
            var verification = new AuthenticationFields();
            var result = await SendCommandAndWaitResponseAsync(BuildAuthenticationCommand(command, attributes),
                token, blockObserver: verification.Observe).ConfigureAwait(false);
            RequireAuthenticationAck(result);
            if (options.Kind == EdlProjectAuthenticationKind.OnePlusDemacia)
            {
                RequireProjectResultZero(verification, command, "verify_res");
            }
            else if (options.Kind != EdlProjectAuthenticationKind.NothingProject)
            {
                RequireProjectResultZero(verification, command, "model_check");
                RequireProjectResultZero(verification, command, "auth_token_verify");
            }
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (options.Kind is EdlProjectAuthenticationKind.OnePlusProject or EdlProjectAuthenticationKind.OnePlusSoftwareProject)
            {
                _onePlusProject = options;
                _onePlusDeviceTimestamp = timestamp;
            }
            return result;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _faulted = true;
            throw new TimeoutException("The project authentication deadline expired; reconnect before continuing.", error);
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

    private static (string Name, string Value)[] ValidateProjectCredentials(EdlProjectAuthenticationKind kind,
        IReadOnlyDictionary<string, byte[]>? credentials)
    {
        var nothing = kind == EdlProjectAuthenticationKind.NothingProject;
        string[] names = nothing ? ["token1", "token2", "token3"] : ["pk", "token"];
        int[] lengths = nothing ? [32, 64, 64]
            : [16, kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject ? 1024 : 512];
        if (credentials is null || credentials.Count != names.Length)
        {
            throw new FirehoseException("The project credential fields do not match the selected protocol.");
        }
        var attributes = new (string Name, string Value)[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            if (!credentials.TryGetValue(names[index], out var bytes) || bytes is null || bytes.Length != lengths[index]
                || bytes.Any(value => names[index] == "pk" ? !char.IsAsciiLetterOrDigit((char)value)
                    : !char.IsAsciiHexDigit((char)value)))
            {
                throw new FirehoseException("A project credential has an invalid length or encoding.");
            }
            attributes[index] = (names[index], Encoding.ASCII.GetString(bytes));
        }
        return attributes;
    }

    private static void RequireProjectResultZero(AuthenticationFields fields, string command, string name)
    {
        if (fields.GetRequired(command, name, name) != "0")
        {
            throw new FirehoseException($"The device rejected project verification ({name}).");
        }
    }
}