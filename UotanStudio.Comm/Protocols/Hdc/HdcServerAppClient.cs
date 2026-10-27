using UotanStudio.Compress.Zip;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// HDC app install and uninstall over a host-server channel using the remote-client mode. Install opens a
/// dedicated host connection, waits for the server's <c>CMD_APP_INIT</c> echo and then streams the package itself as
/// the task master; uninstall sends the command text and reads the server-relayed result text.
/// <para>经宿主 server 通道的 HDC 应用安装与卸载，采用上游 remote-client 模式。安装打开专用宿主连接，等待 server 回送
/// <c>CMD_APP_INIT</c>，随后自行作为任务 master 流式发送包；卸载发送命令文本并读取 server 中转的结果文本。</para>
/// </summary>
/// <remarks>
/// The package is never read from the server host's file system, so a remote server needs no local copy.
/// <para>包内容绝不从 server 主机的文件系统读取，因此远端 server 无需本地副本。</para>
/// </remarks>
public sealed class HdcServerAppClient : Contracts.IHdcAppClient
{
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly string _connectKey;

    /// <summary>
    /// Initializes a server-routed app client for one target.
    /// <para>为单个目标初始化经 server 路由的应用客户端。</para>
    /// </summary>
    /// <param name="server">Host channel client whose address and greeting logic are reused for every task.
    /// <para>每个任务复用其地址与握手逻辑的宿主通道客户端。</para></param>
    /// <param name="connectKey">Target identifier the server must own.
    /// <para>必须由 server 拥有的目标标识。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.
    /// <para><paramref name="server"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="connectKey"/> is blank.
    /// <para><paramref name="connectKey"/> 为空。</para></exception>
    public HdcServerAppClient(HdcServerClient server, string connectKey)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        _serverHost = server.ServerHost;
        _serverPort = server.ServerPort;
        _connectKey = connectKey;
    }

    /// <summary>
    /// Transfers and installs one HAP/HSP/APP package.
    /// <para>传输并安装单个 HAP/HSP/APP 包。</para>
    /// </summary>
    /// <param name="path">Local package file, or a directory that is installed via <see cref="InstallDirectory"/>.
    /// <para>本地包文件；若为目录则经 <see cref="InstallDirectory"/> 安装。</para></param>
    /// <param name="cancellationToken">Cancels before any server exchange begins.
    /// <para>在任何 server 交换开始前取消。</para></param>
    /// <returns>The bundle-manager completion text on success.
    /// <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException">The file extension is not <c>.hap</c>, <c>.hsp</c> or <c>.app</c>.
    /// <para>文件扩展名不是 <c>.hap</c>、<c>.hsp</c> 或 <c>.app</c>。</para></exception>
    /// <exception cref="IOException">The server or daemon reported a failed result.
    /// <para>server 或守护进程报告结果失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string Install(string path, CancellationToken cancellationToken = default)
        => Install(path, "", cancellationToken);

    /// <summary>
    /// Installs a package with bundle-manager options such as -r (replace while preserving data).
    /// <para>安装带 bundle-manager 选项（如 -r：覆盖安装并保留数据）的包。</para>
    /// </summary>
    /// <param name="path">Local package file, or a directory that is installed via <see cref="InstallDirectory"/>.
    /// <para>本地包文件；若为目录则经 <see cref="InstallDirectory"/> 安装。</para></param>
    /// <param name="options">Bundle-manager option string; only ASCII letters, digits, space, <c>-</c>, <c>_</c> and <c>.</c> are accepted.
    /// <para>bundle-manager 选项串；仅接受 ASCII 字母、数字、空格、<c>-</c>、<c>_</c> 与 <c>.</c>。</para></param>
    /// <param name="cancellationToken">Cancels before any server exchange begins.
    /// <para>在任何 server 交换开始前取消。</para></param>
    /// <returns>The bundle-manager completion text on success.
    /// <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException">The extension is unsupported or <paramref name="options"/> contains rejected characters.
    /// <para>扩展名不受支持，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">The server or daemon reported a failed result.
    /// <para>server 或守护进程报告结果失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string Install(string path, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HdcAppClient.ValidateOptions(options);
        if (Directory.Exists(path)) return InstallDirectory(path, options, cancellationToken);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".hap" or ".hsp" or ".app"))
            throw new ArgumentException("Unsupported HDC package extension.", nameof(path));
        return InstallPackage(path, options, cancellationToken);
    }

    /// <summary>
    /// Installs distinct package paths in the supplied order, stopping at the first failure.
    /// <para>按给定顺序安装互不重复的包路径，遇到第一个失败即停止。</para>
    /// </summary>
    /// <param name="paths">Package files or directories; duplicates are collapsed by full path and the batch is capped at 256 entries.
    /// <para>包文件或目录；按完整路径去重，批次上限 256 项。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>.
    /// <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked before each input is normalized and before each install.
    /// <para>在每个输入规范化之前以及每次安装之前检查。</para></param>
    /// <returns>One completion-text entry per installed package, in install order.
    /// <para>按安装顺序为每个已安装包返回一条完成文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.
    /// <para><paramref name="paths"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The batch is empty, exceeds 256 inputs, or <paramref name="options"/> contains rejected characters.
    /// <para>批次为空、输入超过 256 项，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="FileNotFoundException">An input path exists as neither file nor directory.
    /// <para>某个输入路径既不是文件也不是目录。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public IReadOnlyList<string> Install(IEnumerable<string> paths, string options = "", CancellationToken cancellationToken = default)
    {
        HdcAppClient.ValidateOptions(options);
        var packages = HdcAppClient.NormalizeInstallBatch(paths, cancellationToken);
        return [.. packages.Select(path => Install(path, options, cancellationToken))];
    }

    /// <summary>
    /// Installs a bounded package directory as one TAR task so related modules are submitted together.
    /// <para>将有界的包目录作为单个上游 TAR 任务安装，使相关模块一起提交。</para>
    /// </summary>
    /// <param name="path">Directory scanned recursively for <c>.hap</c>, <c>.hsp</c> and <c>.app</c> files; links are never followed.
    /// <para>递归扫描 <c>.hap</c>、<c>.hsp</c>、<c>.app</c> 文件的目录；绝不跟随链接。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>.
    /// <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked during enumeration and TAR writing; the temporary TAR file is deleted either way.
    /// <para>在枚举与写入 TAR 期间检查；临时 TAR 文件无论如何都会删除。</para></param>
    /// <returns>The bundle-manager completion text for the single TAR transfer.
    /// <para>单个 TAR 传输的 bundle-manager 完成文本。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="path"/> is not an existing directory.
    /// <para><paramref name="path"/> 不是现有目录。</para></exception>
    /// <exception cref="IOException">A reparse point was hit, a budget was exceeded, no package was found, or the daemon reported failure.
    /// <para>遇到重分析点、预算超限、未找到包，或守护进程报告失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string InstallDirectory(string path, string options = "", CancellationToken cancellationToken = default)
    {
        HdcAppClient.ValidateOptions(options);
        var root = new DirectoryInfo(path);
        if (!root.Exists) throw new DirectoryNotFoundException(path);
        var packages = HdcAppClient.CollectInstallPackages(root, cancellationToken);
        var tar = HdcAppClient.CreatePackageTar(root, packages, cancellationToken);
        try
        {
            return InstallPackage(tar, options, cancellationToken);
        }
        finally { File.Delete(tar); }
    }

    /// <summary>
    /// Installs the signed modules in an APP container that match a device type, without re-signing them.
    /// <para>安装 APP 容器中与目标设备类型匹配的已签名模块，不进行重新签名。</para>
    /// </summary>
    /// <param name="path">APP (ZIP) container path opened read-only.
    /// <para>以只读方式打开的 APP（ZIP）容器路径。</para></param>
    /// <param name="deviceType">Device type matched against the <c>deviceType</c> arrays in <c>pack.info</c>.
    /// <para>与 <c>pack.info</c> 中各 <c>deviceType</c> 数组匹配的机型代号。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>.
    /// <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked while extracting modules; the staging directory is deleted either way.
    /// <para>解压模块期间检查；暂存目录无论如何都会删除。</para></param>
    /// <returns>The bundle-manager completion text for the single-module install or the combined TAR transfer.
    /// <para>单模块安装或合并 TAR 传输的 bundle-manager 完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="deviceType"/> is blank or <paramref name="options"/> contains rejected characters.
    /// <para><paramref name="deviceType"/> 为空，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="InvalidDataException">The container, its <c>pack.info</c> metadata or a module entry violates the size, naming or uniqueness constraints.
    /// <para>容器、其 <c>pack.info</c> 元数据或模块条目违反大小、命名或唯一性约束。</para></exception>
    /// <exception cref="NotSupportedException">The container has no installable module for <paramref name="deviceType"/>.
    /// <para>容器中没有匹配 <paramref name="deviceType"/> 的可安装模块。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string InstallAppBundle(string path, string deviceType, string options = "", CancellationToken cancellationToken = default)
    {
        HdcAppClient.ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipContainer.OpenRead(path);
        var staging = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var package = HdcAppClient.ExtractAppBundle(archive, deviceType, staging, cancellationToken);
            return InstallPackage(package, options, cancellationToken);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    /// <summary>
    /// Uninstalls the exact named bundle.
    /// <para>卸载指定精确名称的 bundle。</para>
    /// </summary>
    /// <param name="bundle">Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted.
    /// <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para></param>
    /// <param name="cancellationToken">Cancels before the uninstall command is sent.
    /// <para>在发送卸载命令前取消。</para></param>
    /// <returns>The bundle-manager completion text on success.
    /// <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> is blank or contains rejected characters.
    /// <para><paramref name="bundle"/> 为空或含被拒绝的字符。</para></exception>
    /// <exception cref="IOException">The server or daemon reported a failed result.
    /// <para>server 或守护进程报告结果失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string Uninstall(string bundle, CancellationToken cancellationToken = default)
        => Uninstall(bundle, "", cancellationToken);

    /// <summary>
    /// Uninstalls a bundle with options such as -k (keep data), without changing default behavior.
    /// <para>带选项（如 -k：保留数据）卸载 bundle，不改变默认行为。</para>
    /// </summary>
    /// <param name="bundle">Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted.
    /// <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para></param>
    /// <param name="options">Bundle-manager option string; when empty only the bundle name is sent.
    /// <para>bundle-manager 选项串；为空时仅发送 bundle 名称。</para></param>
    /// <param name="cancellationToken">Cancels before the uninstall command is sent.
    /// <para>在发送卸载命令前取消。</para></param>
    /// <returns>The bundle-manager completion text on success.
    /// <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> or <paramref name="options"/> contains rejected characters.
    /// <para><paramref name="bundle"/> 或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="IOException">The server or daemon reported a failed result.
    /// <para>server 或守护进程报告结果失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string Uninstall(string bundle, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HdcAppClient.ValidateOptions(options);
        HdcAppClient.ValidateBundle(bundle);
        var command = options.Length == 0 ? "uninstall " + bundle : "uninstall " + options + " " + bundle;
        using var channel = OpenChannel(command, cancellationToken);
        var (text, appResult) = channel.ReadServerResult(300000, cancellationToken);
        return appResult is null ? HdcAppClient.ValidateUninstallText(text) : HdcAppClient.ParseResult(appResult, 2);
    }

    private string InstallPackage(string path, string options, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var input = File.OpenRead(path);
        var config = HdcAppClient.BuildInstallConfig(input.Length, extension, options);
        using var channel = OpenChannel(HdcServerRemoteCommand.Build("install", path), cancellationToken);
        HdcTransferEngine.Expect(channel, HdcCommand.AppInit, cancellationToken);
        channel.SendCommand(HdcCommand.WakeupSlaveTask, []);
        channel.SendCommand(HdcCommand.AppCheck, HdcTransferCodec.EncodeConfig(config));
        HdcTransferEngine.Expect(channel, HdcCommand.AppBegin, cancellationToken);
        HdcTransferEngine.SendData(channel, input, input.Length, HdcCommand.AppData, false, null, cancellationToken);
        return HdcAppClient.ReadResult(channel, 1, cancellationToken);
    }

    private HdcServerTaskChannel OpenChannel(string command, CancellationToken cancellationToken)
    {
        using var server = new HdcServerClient(_serverHost, _serverPort);
        return HdcServerTaskChannel.OpenAsync(server, _connectKey, command, cancellationToken).GetAwaiter().GetResult();
    }
}