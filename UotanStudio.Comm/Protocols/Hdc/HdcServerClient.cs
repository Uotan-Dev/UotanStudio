using System.Diagnostics;
using System.Text;
using UotanStudio.Comm.Transports.Tcp;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Client for interacting with the local HDC server (similar to ADB server).
/// The HDC server is a background process running on the development machine that
/// manages USB/TCP device connections. Clients communicate with the server over TCP,
/// and the server forwards commands to the target devices.
/// <para>用于与本地 HDC server（类似 ADB server）交互的客户端。
/// HDC server 是运行在开发机上的后台进程，负责管理 USB/TCP 设备连接。
/// 客户端通过 TCP 连接 server，server 将命令转发到目标设备。</para>
/// </summary>
public sealed class HdcServerClient : IDisposable
{
    /// <summary>
    /// The expected banner string returned by the HDC server during handshake.
    /// <para>HDC server 握手时返回的预期 banner 字符串。</para>
    /// </summary>
    private const string ExpectedBanner = "OHOS HDC";

    private HdcTcpTransport? _transport;
    private HdcConnection? _connection;
    private readonly string _serverHost;
    private readonly int _serverPort;
    private bool _disposed;

    /// <summary>
    /// Gets the HDC server host address.
    /// <para>获取 HDC server 的主机地址。</para>
    /// </summary>
    public string ServerHost => _serverHost;

    /// <summary>
    /// Gets the HDC server TCP port.
    /// <para>获取 HDC server 的 TCP 端口。</para>
    /// </summary>
    public int ServerPort => _serverPort;

    /// <summary>
    /// Initializes a new HDC server client targeting the specified host and port.
    /// <para>初始化指向指定主机和端口的 HDC server 客户端。</para>
    /// </summary>
    /// <param name="host">
    /// The HDC server host address; defaults to <c>"127.0.0.1"</c>.
    /// <para>HDC server 主机地址，默认 <c>"127.0.0.1"</c>。</para>
    /// </param>
    /// <param name="port">
    /// The HDC server TCP port; defaults to 8710.
    /// <para>HDC server TCP 端口，默认 8710。</para>
    /// </param>
    public HdcServerClient(string host = "127.0.0.1", int port = 8710)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        _serverHost = host;
        _serverPort = port;
    }

    /// <summary>
    /// Detects whether the HDC server is running by attempting a TCP connection and
    /// verifying the handshake banner.
    /// <para>通过尝试 TCP 连接并验证握手 banner 来检测 HDC server 是否正在运行。</para>
    /// </summary>
    /// <param name="timeoutMs">
    /// Timeout in milliseconds for the detection attempt; defaults to 2000.
    /// <para>检测尝试的超时时间（毫秒），默认 2000。</para>
    /// </param>
    /// <returns>
    /// <c>true</c> if the server responded with the expected banner; otherwise <c>false</c>.
    /// <para>如果 server 返回了预期 banner 则为 <c>true</c>，否则为 <c>false</c>。</para>
    /// </returns>
    public bool DetectServer(int timeoutMs = 2000)
    {
        ThrowIfDisposed();

        HdcTcpTransport? probe = null;
        try
        {
            probe = new HdcTcpTransport(_serverHost, _serverPort, timeoutMs);

            // Send handshake probe: write a minimal connect command.
            var handshake = Encoding.ASCII.GetBytes("OHOS HDC");
            probe.Write(handshake, 0, handshake.Length, timeoutMs);

            // Read the server banner response.
            var bannerBuffer = new byte[256];
            var bytesRead = probe.Read(bannerBuffer, 0, bannerBuffer.Length, timeoutMs);
            if (bytesRead <= 0)
            {
                return false;
            }

            var banner = Encoding.ASCII.GetString(bannerBuffer, 0, bytesRead).TrimEnd('\0');
            return banner.Contains(ExpectedBanner, StringComparison.OrdinalIgnoreCase);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.IO.IOException)
        {
            return false;
        }
        finally
        {
            probe?.Dispose();
        }
    }

    /// <summary>
    /// Quick check whether the HDC server is currently running.
    /// <para>快速检测 HDC server 是否正在运行。</para>
    /// </summary>
    /// <returns>
    /// <c>true</c> if the server is reachable and responds correctly; otherwise <c>false</c>.
    /// <para>如果 server 可达且正确响应则为 <c>true</c>，否则为 <c>false</c>。</para>
    /// </returns>
    public bool IsServerRunning()
    {
        return DetectServer();
    }

    /// <summary>
    /// Connects to the HDC server, performing authentication and handshake.
    /// <para>连接到 HDC server，执行认证和握手。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the connection has already been established.
    /// <para>如果连接已经建立则抛出。</para>
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown if the connection attempt times out.
    /// <para>如果连接尝试超时则抛出。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown if the server rejects the connection or the handshake fails.
    /// <para>如果 server 拒绝连接或握手失败则抛出。</para>
    /// </exception>
    public void Connect()
    {
        ThrowIfDisposed();

        if (_connection is not null)
        {
            throw new InvalidOperationException("Already connected to the HDC server. Dispose the existing connection first.");
        }

        _transport = new HdcTcpTransport(_serverHost, _serverPort);
        try
        {
            var auth = HdcAuthentication.LoadOrCreate();
            _connection = new HdcConnection(_transport, auth);
            _connection.Connect();
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            _transport?.Dispose();
            _transport = null;
            throw;
        }
    }

    /// <summary>
    /// Connects to the HDC server asynchronously, performing authentication and handshake.
    /// <para>异步连接到 HDC server，执行认证和握手。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous connect operation. <para>表示异步连接操作的任务。</para></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_connection is not null)
        {
            throw new InvalidOperationException("Already connected to the HDC server. Dispose the existing connection first.");
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connect();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all connected device target keys by sending a <c>TargetList</c> command to the HDC server.
    /// <para>通过向 HDC server 发送 <c>TargetList</c> 命令列出所有已连接设备的连接键。</para>
    /// </summary>
    /// <param name="timeoutMs">
    /// Timeout in milliseconds; defaults to 10000.
    /// <para>超时时间（毫秒），默认 10000。</para>
    /// </param>
    /// <returns>
    /// A list of device connection key strings (e.g., <c>"192.168.1.100:8710"</c> or USB serial).
    /// <para>设备连接键字符串列表（如 <c>"192.168.1.100:8710"</c> 或 USB 序列号）。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if not connected to the HDC server.
    /// <para>如果未连接到 HDC server 则抛出。</para>
    /// </exception>
    public IReadOnlyList<string> ListTargets(int timeoutMs = 10000)
    {
        EnsureConnected();

        // The HDC protocol uses a "list" transport command to enumerate targets.
        // Build and send the list targets command frame.
        var cmdBytes = Encoding.UTF8.GetBytes("list targets");
        var transportStream = _transport!.GetStream();

        // Write the command.
        transportStream.WriteTimeout = timeoutMs;
        transportStream.Write(cmdBytes, 0, cmdBytes.Length);

        // Read the response.
        transportStream.ReadTimeout = timeoutMs;
        var responseBuffer = new byte[4096];
        var totalRead = 0;
        do
        {
            var read = transportStream.Read(responseBuffer, totalRead, responseBuffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }
        while (transportStream is System.Net.Sockets.NetworkStream ns && ns.DataAvailable);

        if (totalRead == 0)
        {
            return Array.Empty<string>();
        }

        var response = Encoding.UTF8.GetString(responseBuffer, 0, totalRead).Trim();
        if (string.IsNullOrEmpty(response))
        {
            return Array.Empty<string>();
        }

        // Response format: each device on a separate line, tab-separated (serial\tstate).
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var targets = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Extract the connection key (first column before tab).
            var tabIndex = trimmed.IndexOf('\t');
            targets.Add(tabIndex > 0 ? trimmed[..tabIndex] : trimmed);
        }

        return targets;
    }

    /// <summary>
    /// Executes a command on a specific device through the HDC server.
    /// <para>通过 HDC server 在指定设备上执行命令。</para>
    /// </summary>
    /// <param name="connectKey">
    /// The device connection key (as returned by <see cref="ListTargets"/>).
    /// <para>设备连接键（由 <see cref="ListTargets"/> 返回）。</para>
    /// </param>
    /// <param name="command">
    /// The command string to execute on the device (e.g., <c>"shell:ls /"</c>).
    /// <para>在设备上执行的命令字符串（如 <c>"shell:ls /"</c>）。</para>
    /// </param>
    /// <param name="timeoutMs">
    /// Timeout in milliseconds; defaults to 30000.
    /// <para>超时时间（毫秒），默认 30000。</para>
    /// </param>
    /// <returns>
    /// The raw command output bytes.
    /// <para>命令输出的原始字节。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if not connected to the HDC server.
    /// <para>如果未连接到 HDC server 则抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="connectKey"/> or <paramref name="command"/> is empty.
    /// <para>如果 <paramref name="connectKey"/> 或 <paramref name="command"/> 为空则抛出。</para>
    /// </exception>
    public byte[] ExecuteOnDevice(string connectKey, string command, int timeoutMs = 30000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        EnsureConnected();

        var transportStream = _transport!.GetStream();

        // First, select the target device via TargetConnect.
        var targetCmd = Encoding.UTF8.GetBytes($"target:{connectKey}");
        transportStream.WriteTimeout = timeoutMs;
        transportStream.Write(targetCmd, 0, targetCmd.Length);

        // Read target connect acknowledgement.
        transportStream.ReadTimeout = timeoutMs;
        var ackBuffer = new byte[1024];
        var ackRead = transportStream.Read(ackBuffer, 0, ackBuffer.Length);

        // Now send the actual command.
        var cmdBytes = Encoding.UTF8.GetBytes(command);
        transportStream.Write(cmdBytes, 0, cmdBytes.Length);

        // Read the command response.
        using var outputStream = new MemoryStream();
        transportStream.ReadTimeout = timeoutMs;
        var readBuffer = new byte[8192];
        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = transportStream.Read(readBuffer, 0, readBuffer.Length);
            }
            catch (TimeoutException)
            {
                // Treat timeout as end of command output.
                break;
            }

            if (bytesRead == 0)
            {
                break;
            }

            outputStream.Write(readBuffer, 0, bytesRead);

            // Stop reading if no more data is immediately available.
            if (transportStream is not System.Net.Sockets.NetworkStream ns2 || !ns2.DataAvailable)
            {
                break;
            }
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Starts the HDC server process if it is not already running.
    /// <para>如果 HDC server 尚未运行，则启动它。</para>
    /// </summary>
    /// <remarks>
    /// Looks for the <c>hdc</c> executable in PATH and runs <c>hdc start -s</c>.
    /// Falls back to <c>hdc start</c> if <c>-s</c> is not supported.
    /// <para>在 PATH 中查找 <c>hdc</c> 可执行文件并运行 <c>hdc start -s</c>。
    /// 如果 <c>-s</c> 不支持则回退到 <c>hdc start</c>。</para>
    /// </remarks>
    /// <exception cref="FileNotFoundException">
    /// Thrown if the <c>hdc</c> executable cannot be found.
    /// <para>如果找不到 <c>hdc</c> 可执行文件则抛出。</para>
    /// </exception>
    public void StartServer()
    {
        ThrowIfDisposed();

        // Already running?
        if (DetectServer(1000))
        {
            return;
        }

        var hdcPath = FindHdcExecutable()
            ?? throw new FileNotFoundException(
                "Cannot find 'hdc' executable. Ensure it is installed and available in PATH.",
                "hdc");

        // Try "hdc start -s" first; fall back to "hdc start".
        var exitCode = StartHdcProcess("start -s");
        if (exitCode != 0)
        {
            StartHdcProcess("start");
        }

        // Wait for the server to become ready (up to 5 seconds).
        const int maxWaitMs = 5000;
        const int pollIntervalMs = 200;
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            if (DetectServer(1000))
            {
                return;
            }

            Thread.Sleep(pollIntervalMs);
            elapsed += pollIntervalMs;
        }

        throw new TimeoutException("HDC server did not become ready within the expected time.");
    }

    /// <summary>
    /// Terminates the HDC server by sending a kill command.
    /// <para>通过发送终止命令来终止 HDC server。</para>
    /// </summary>
    public void KillServer()
    {
        ThrowIfDisposed();

        HdcTcpTransport? killTransport = null;
        try
        {
            killTransport = new HdcTcpTransport(_serverHost, _serverPort, connectTimeoutMs: 3000);
            var killCmd = Encoding.UTF8.GetBytes("server kill");
            killTransport.Write(killCmd, 0, killCmd.Length, 3000);
        }
        catch
        {
            // Server may have already shut down; this is expected.
        }
        finally
        {
            killTransport?.Dispose();
        }
    }

    /// <summary>
    /// Creates an <see cref="HdcSession"/> for a specific device through the HDC server.
    /// <para>通过 HDC server 为指定设备创建 <see cref="HdcSession"/> 会话。</para>
    /// </summary>
    /// <param name="connectKey">
    /// The device connection key (as returned by <see cref="ListTargets"/>).
    /// <para>设备连接键（由 <see cref="ListTargets"/> 返回）。</para>
    /// </param>
    /// <returns>
    /// A new <see cref="HdcSession"/> connected to the specified device.
    /// <para>连接到指定设备的新 <see cref="HdcSession"/>。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if not connected to the HDC server.
    /// <para>如果未连接到 HDC server 则抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="connectKey"/> is empty.
    /// <para>如果 <paramref name="connectKey"/> 为空则抛出。</para>
    /// </exception>
    public HdcSession CreateSession(string connectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        EnsureConnected();

        // Create a dedicated transport for the device session.
        var deviceTransport = new HdcTcpTransport(_serverHost, _serverPort);

        try
        {
            // Send target selection on the new transport.
            var targetCmd = Encoding.UTF8.GetBytes($"target:{connectKey}");
            deviceTransport.Write(targetCmd, 0, targetCmd.Length, 10000);

            // Read acknowledgement.
            var ackBuffer = new byte[1024];
            deviceTransport.Read(ackBuffer, 0, ackBuffer.Length, 10000);

            // Create the session using the device-scoped transport.
            var auth = HdcAuthentication.LoadOrCreate();
            var connection = new HdcConnection(deviceTransport, auth);
            connection.Connect();

            return new HdcSession(deviceTransport, auth);
        }
        catch
        {
            deviceTransport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases all resources held by this client, including the underlying transport
    /// and connection to the HDC server.
    /// <para>释放此客户端持有的所有资源，包括到底层传输和 HDC server 的连接。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _connection?.Dispose();
        _connection = null;

        _transport?.Dispose();
        _transport = null;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Searches for the <c>hdc</c> executable in the system PATH.
    /// <para>在系统 PATH 中查找 <c>hdc</c> 可执行文件。</para>
    /// </summary>
    /// <returns>
    /// The full path to the <c>hdc</c> executable, or <c>null</c> if not found.
    /// <para><c>hdc</c> 可执行文件的完整路径，未找到时返回 <c>null</c>。</para>
    /// </returns>
    internal static string? FindHdcExecutable()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var exeName = OperatingSystem.IsWindows() ? "hdc.exe" : "hdc";

        foreach (var dir in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var fullPath = Path.Combine(dir, exeName);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Starts an HDC process with the specified arguments and returns the exit code.
    /// <para>使用指定参数启动 HDC 进程并返回退出码。</para>
    /// </summary>
    /// <param name="args">The arguments to pass to the <c>hdc</c> executable. <para>传递给 <c>hdc</c> 可执行文件的参数。</para></param>
    /// <returns>The process exit code. <para>进程退出码。</para></returns>
    internal int StartHdcProcess(string args)
    {
        var hdcPath = FindHdcExecutable()
            ?? throw new FileNotFoundException(
                "Cannot find 'hdc' executable. Ensure it is installed and available in PATH.",
                "hdc");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = hdcPath,
            // AOT-compatible: use ArgumentList instead of a shell-parsed string.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Add arguments individually via ArgumentList for AOT compatibility.
        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
            process.WaitForExit(10_000);
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Ensures the client is connected to the HDC server.
    /// <para>确保客户端已连接到 HDC server。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the client is not connected.
    /// <para>如果客户端未连接则抛出。</para>
    /// </exception>
    private void EnsureConnected()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected to the HDC server. Call Connect() or ConnectAsync() first.");
        }
    }

    /// <summary>
    /// Throws if this client instance has been disposed.
    /// <para>如果此客户端实例已被释放则抛出异常。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the client has been disposed.
    /// <para>如果客户端已被释放则抛出。</para>
    /// </exception>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HdcServerClient));
        }
    }
}
