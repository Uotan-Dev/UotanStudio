using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>
/// Device operations the wrapped command runner needs. The seam keeps every handler testable with a
/// fake gateway while production code goes through the host's own <see cref="DeviceMonitorService"/>,
/// so wrapped commands never open a competing transport.
/// <para>包装命令执行器所需的设备操作。该缝隙让每个处理器都能用假 gateway 测试，而生产实现走宿主自己的
/// <see cref="DeviceMonitorService"/>，包装命令绝不会另开竞争传输。</para>
/// </summary>
internal interface IWrappedCommandGateway
{
    /// <summary>Scans the current device list.<para>扫描当前设备列表。</para></summary>
    /// <returns>The detected endpoints.<para>探测到的端点。</para></returns>
    IReadOnlyList<DetectedDevice> Scan();

    /// <summary>Executes one shell command on the ADB target and captures its output.
    /// <para>在 ADB 目标上执行一条 shell 命令并捕获输出。</para></summary>
    /// <param name="device">The ADB target.<para>ADB 目标。</para></param>
    /// <param name="command">The device shell command line.<para>设备 shell 命令行。</para></param>
    /// <param name="cancellationToken">Cancels the command.<para>取消命令。</para></param>
    /// <returns>The exit code and captured stdout/stderr text.<para>退出码与捕获的 stdout/stderr 文本。</para></returns>
    Task<(int ExitCode, string Output)> ExecuteAdbShellAsync(DetectedDevice device, string command,
        CancellationToken cancellationToken = default);

    /// <summary>Streams one ADB shell command's output chunk by chunk, for logcat style commands that
    /// never terminate. <para>逐块回调一条 ADB shell 命令的输出，用于 logcat 这类不退出的命令。</para></summary>
    /// <param name="device">The ADB target.<para>ADB 目标。</para></param>
    /// <param name="command">The device shell command line.<para>设备 shell 命令行。</para></param>
    /// <param name="onChunk">Callback receiving each raw chunk.<para>接收每个原始块的回调。</para></param>
    /// <param name="cancellationToken">Cancels the stream.<para>取消流。</para></param>
    /// <returns>The device exit code when reported.<para>设备上报的退出码。</para></returns>
    Task<int> StreamAdbShellAsync(DetectedDevice device, string command, Action<byte[]> onChunk,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a one-shot ADB host service such as <c>root:</c> and returns its text response.
    /// <para>运行一次性的 ADB host 服务（如 <c>root:</c>）并返回文本响应。</para></summary>
    /// <param name="serial">Serial of the ADB target.<para>ADB 目标序列号。</para></param>
    /// <param name="service">Service destination such as <c>root:</c>.<para>服务目标，如 <c>root:</c>。</para></param>
    /// <returns>The response text.<para>响应文本。</para></returns>
    string RunAdbService(string serial, string service);

    /// <summary>Pushes one local file to the device.<para>把一个本地文件推送到设备。</para></summary>
    /// <param name="device">The ADB target.<para>ADB 目标。</para></param>
    /// <param name="localPath">Local source file.<para>本地源文件。</para></param>
    /// <param name="remotePath">Device destination.<para>设备目标路径。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    Task PushAdbFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default);

    /// <summary>Pulls one remote file to the host.<para>把一个设备文件拉取到本机。</para></summary>
    /// <param name="device">The ADB target.<para>ADB 目标。</para></param>
    /// <param name="remotePath">Device source path.<para>设备源路径。</para></param>
    /// <param name="localPath">Local destination file.<para>本地目标文件。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    Task PullAdbFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default);

    /// <summary>Installs one APK package by pushing it and invoking the package manager.
    /// <para>通过推送并调用包管理器安装一个 APK 包。</para></summary>
    /// <param name="device">The ADB target.<para>ADB 目标。</para></param>
    /// <param name="apkPath">Local APK path.<para>本地 APK 路径。</para></param>
    /// <param name="options">Extra <c>pm install</c> options such as <c>-r</c>.<para>额外的 <c>pm install</c> 选项，如 <c>-r</c>。</para></param>
    /// <param name="cancellationToken">Cancels the install.<para>取消安装。</para></param>
    /// <returns>The package manager output and whether it succeeded.<para>包管理器输出与是否成功。</para></returns>
    Task<(bool Success, string Output)> InstallAdbAsync(DetectedDevice device, string apkPath, string options,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one package through the device package manager.<para>经设备包管理器移除一个包。</para></summary>
    /// <param name="device">The ADB target.<para>ADB 目标。</para></param>
    /// <param name="package">Package name.<para>包名。</para></param>
    /// <param name="keepData">Whether <c>-k</c> keeps app data.<para>是否以 <c>-k</c> 保留应用数据。</para></param>
    /// <param name="cancellationToken">Cancels the removal.<para>取消移除。</para></param>
    /// <returns>The package manager output and whether it succeeded.<para>包管理器输出与是否成功。</para></returns>
    Task<(bool Success, string Output)> UninstallAdbAsync(DetectedDevice device, string package, bool keepData,
        CancellationToken cancellationToken = default);

    /// <summary>Reboots the target into the requested mode.<para>重启目标到指定模式。</para></summary>
    /// <param name="device">The target device.<para>目标设备。</para></param>
    /// <param name="mode">Mode name such as <c>bootloader</c>, or <c>null</c> for a plain reboot.
    /// <para>模式名（如 <c>bootloader</c>）；<c>null</c> 为普通重启。</para></param>
    /// <param name="cancellationToken">Cancels the wait.<para>取消等待。</para></param>
    /// <returns>Whether the reboot command succeeded.<para>重启命令是否成功。</para></returns>
    Task<bool> RebootAsync(DetectedDevice device, string? mode, CancellationToken cancellationToken = default);

    /// <summary>Connects a network debug-bridge target.<para>连接网络调试桥目标。</para></summary>
    /// <param name="protocol">The bridge protocol.<para>调试桥协议。</para></param>
    /// <param name="address">Host or host:port address.<para>主机或 host:port 地址。</para></param>
    /// <param name="cancellationToken">Cancels the connect.<para>取消连接。</para></param>
    Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnects an ADB network target, or every one when no address is given.
    /// <para>断开一个 ADB 网络目标；未给地址时断开全部。</para></summary>
    /// <param name="address">The address to disconnect, or <c>null</c> for all.<para>要断开的地址；<c>null</c> 表示全部。</para></param>
    /// <param name="cancellationToken">Cancels the disconnect.<para>取消断开。</para></param>
    Task DisconnectAdbAsync(string? address, CancellationToken cancellationToken = default);

    /// <summary>Pairs with a wireless debugging target.<para>与无线调试目标配对。</para></summary>
    /// <param name="address">Pairing host:port address.<para>配对 host:port 地址。</para></param>
    /// <param name="code">The six digit pairing code.<para>六位配对码。</para></param>
    /// <param name="cancellationToken">Cancels the pairing.<para>取消配对。</para></param>
    /// <returns>The server pairing response.<para>server 配对响应。</para></returns>
    Task<string> PairAdbAsync(string address, string code, CancellationToken cancellationToken = default);

    /// <summary>Stops the local ADB server.<para>停止本机 ADB server。</para></summary>
    /// <param name="cancellationToken">Cancels the stop.<para>取消停止。</para></param>
    Task StopAdbServerAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the in-app device shell terminal for the target.
    /// <para>为目标打开应用内设备 shell 终端。</para></summary>
    /// <param name="device">The ADB or HDC target.<para>ADB 或 HDC 目标。</para></param>
    Task OpenDeviceShellAsync(DetectedDevice device);

    /// <summary>Reads one fastboot variable from the target.<para>从目标读取一个 fastboot 变量。</para></summary>
    /// <param name="device">The Fastboot target.<para>Fastboot 目标。</para></param>
    /// <param name="key">Variable name such as <c>product</c>.<para>变量名，如 <c>product</c>。</para></param>
    /// <returns>The value and whether the bootloader reported it.<para>变量值与 boot 是否上报。</para></returns>
    (string Value, bool Found) GetFastbootVariable(DetectedDevice device, string key);

    /// <summary>Reads every fastboot variable the bootloader reports.<para>读取 boot 上报的全部 fastboot 变量。</para></summary>
    /// <param name="device">The Fastboot target.<para>Fastboot 目标。</para></param>
    /// <returns>The variable table.<para>变量表。</para></returns>
    Dictionary<string, string> GetFastbootAllVariables(DetectedDevice device);

    /// <summary>Runs one raw fastboot command such as <c>erase:boot</c> or <c>oem unlock</c>.
    /// <para>运行一条原始 fastboot 命令，如 <c>erase:boot</c> 或 <c>oem unlock</c>。</para></summary>
    /// <param name="device">The Fastboot target.<para>Fastboot 目标。</para></param>
    /// <param name="command">The raw command line.<para>原始命令行。</para></param>
    /// <param name="cancellationToken">Cancels the wait.<para>取消等待。</para></param>
    /// <returns>The terminal fastboot response with its INFO lines.<para>带 INFO 行的终止 fastboot 响应。</para></returns>
    FastbootResponse RunFastbootCommand(DetectedDevice device, string command,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads one image into the bootloader's download buffer.
    /// <para>把一个镜像下载进 boot 的下载缓冲。</para></summary>
    /// <param name="device">The Fastboot target.<para>Fastboot 目标。</para></param>
    /// <param name="stream">Image bytes read to the given length.<para>读至给定长度的镜像字节。</para></param>
    /// <param name="length">Exact byte count to send.<para>要发送的精确字节数。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    /// <returns>The download response.<para>下载响应。</para></returns>
    FastbootResponse FastbootDownload(DetectedDevice device, Stream stream, long length,
        CancellationToken cancellationToken = default);

    /// <summary>Executes one shell command on the HDC target.<para>在 HDC 目标上执行一条 shell 命令。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="command">The device shell command line.<para>设备 shell 命令行。</para></param>
    /// <param name="cancellationToken">Cancels the command.<para>取消命令。</para></param>
    /// <returns>The captured output.<para>捕获的输出。</para></returns>
    Task<string> ExecuteHdcShellAsync(DetectedDevice device, string command,
        CancellationToken cancellationToken = default);

    /// <summary>Streams one HDC shell command's output chunk by chunk.<para>逐块回调一条 HDC shell 命令的输出。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="command">The device shell command line.<para>设备 shell 命令行。</para></param>
    /// <param name="onChunk">Callback receiving each raw chunk.<para>接收每个原始块的回调。</para></param>
    /// <param name="cancellationToken">Cancels the stream.<para>取消流。</para></param>
    /// <returns>The device exit code when reported.<para>设备上报的退出码。</para></returns>
    Task<int> StreamHdcShellAsync(DetectedDevice device, string command, Action<byte[]> onChunk,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a local file to the HDC device.<para>把本地文件发送到 HDC 设备。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="localPath">Local source file.<para>本地源文件。</para></param>
    /// <param name="remotePath">Device destination.<para>设备目标路径。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    Task HdcSendFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default);

    /// <summary>Receives a device file from the HDC device.<para>从 HDC 设备接收文件。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="remotePath">Device source path.<para>设备源路径。</para></param>
    /// <param name="localPath">Local destination file.<para>本地目标文件。</para></param>
    /// <param name="cancellationToken">Cancels the transfer.<para>取消传输。</para></param>
    Task HdcReceiveFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default);

    /// <summary>Installs one HAP/HSP package on the HDC device.<para>在 HDC 设备上安装一个 HAP/HSP 包。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="path">Local package path.<para>本地包路径。</para></param>
    /// <param name="replace">Whether <c>-r</c> replaces an existing bundle.<para>是否以 <c>-r</c> 替换既有包。</para></param>
    /// <param name="cancellationToken">Cancels the install.<para>取消安装。</para></param>
    /// <returns>The installer output.<para>安装器输出。</para></returns>
    Task<string> HdcInstallAsync(DetectedDevice device, string path, bool replace,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one bundle from the HDC device.<para>从 HDC 设备移除一个包。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="bundle">Bundle name.<para>包名。</para></param>
    /// <param name="keepData">Whether <c>-k</c> keeps bundle data.<para>是否以 <c>-k</c> 保留包数据。</para></param>
    /// <param name="cancellationToken">Cancels the removal.<para>取消移除。</para></param>
    /// <returns>The uninstaller output.<para>卸载器输出。</para></returns>
    Task<string> HdcUninstallAsync(DetectedDevice device, string bundle, bool keepData,
        CancellationToken cancellationToken = default);

    /// <summary>Reboots the HDC target into the requested mode.<para>重启 HDC 目标到指定模式。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="mode">Mode name such as <c>bootloader</c>, or <c>null</c>.<para>模式名（如 <c>bootloader</c>）；可为 <c>null</c>。</para></param>
    /// <param name="cancellationToken">Cancels the wait.<para>取消等待。</para></param>
    Task HdcRebootAsync(DetectedDevice device, string? mode, CancellationToken cancellationToken = default);

    /// <summary>Disconnects one HDC target.<para>断开一个 HDC 目标。</para></summary>
    /// <param name="device">The HDC target.<para>HDC 目标。</para></param>
    /// <param name="cancellationToken">Cancels the disconnect.<para>取消断开。</para></param>
    Task HdcDisconnectAsync(DetectedDevice device, CancellationToken cancellationToken = default);

    /// <summary>Stops the local HDC server.<para>停止本机 HDC server。</para></summary>
    /// <param name="cancellationToken">Cancels the stop.<para>取消停止。</para></param>
    Task StopHdcServerAsync(CancellationToken cancellationToken = default);
}