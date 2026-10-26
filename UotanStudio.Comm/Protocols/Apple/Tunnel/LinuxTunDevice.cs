using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// Linux TUN backend: opens <c>/dev/net/tun</c>, attaches a non-persistent TUN interface through
/// the TUNSETIFF ioctl (IFF_TUN|IFF_NO_PI, raw IPv6 packets without the packet-info header) and
/// configures address, MTU and link state with the <c>ip</c> command. Creating the interface and
/// running <c>ip</c> requires root; every failure throws with the failing command output.
/// <para>Linux TUN 后端：打开 <c>/dev/net/tun</c>，经 TUNSETIFF ioctl（IFF_TUN|IFF_NO_PI，不带
/// packet-info 头的原始 IPv6 报文）挂载非持久 TUN 接口，并用 <c>ip</c> 命令配置地址、MTU 与链路
/// 状态。创建接口与执行 <c>ip</c> 需要 root；任何失败都会带命令输出抛出。</para>
/// </summary>
internal sealed class LinuxTunDevice : ITunDevice
{
    private readonly byte[] _receiveBuffer = new byte[Interop.MaxIpPacketSize + 1];
    private readonly Lock _gate = new();
    private bool _disposed;

    private LinuxTunDevice(SafeFileHandle handle, string name)
    {
        Handle = handle;
        Name = name;
    }

    /// <summary>Gets the interface name confirmed by the kernel. <para>获取内核确认的接口名。</para></summary>
    public string Name { get; }

    private SafeFileHandle Handle { get; }

    /// <summary>
    /// Opens the TUN device, attaches the interface and configures it via <c>ip</c>.
    /// <para>打开 TUN 设备、挂载接口并经 <c>ip</c> 配置。</para>
    /// </summary>
    /// <param name="name">The requested interface name, truncated to 15 characters; empty lets the
    /// kernel assign <c>tun%d</c>. <para>请求的接口名，截断到 15 字符；为空则由内核分配
    /// <c>tun%d</c>。</para></param>
    /// <param name="clientAddress">The tunnel client IPv6 address. <para>隧道本端 IPv6 地址。</para></param>
    /// <param name="prefixLength">The on-link prefix length. <para>链路前缀长度。</para></param>
    /// <param name="mtu">The negotiated interface MTU; zero keeps the kernel default.
    /// <para>协商的接口 MTU；零保持内核默认。</para></param>
    /// <param name="cancellationToken">Cancels the configuration. <para>取消配置。</para></param>
    /// <returns>The opened device. <para>已打开的设备。</para></returns>
    /// <exception cref="IOException">The TUN device cannot be opened or the interface configuration
    /// failed. <para>TUN 设备无法打开或接口配置失败时抛出。</para></exception>
    internal static LinuxTunDevice Open(string name, string clientAddress, int prefixLength, int mtu,
        CancellationToken cancellationToken)
    {
        var requestedName = name.Length > Interop.InterfaceNameBytes - 1
            ? name[..(Interop.InterfaceNameBytes - 1)]
            : name;
        var fd = Interop.Open(Interop.TunDevicePath, Interop.OpenReadWrite | Interop.OpenCloseOnExec);
        if (fd < 0)
        {
            throw new IOException(
                $"open({Interop.TunDevicePath}) failed with errno {Marshal.GetLastWin32Error()}; " +
                "the TUN char device requires root privileges and the tun kernel module.");
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        var attachedName = AttachInterface(fd, requestedName);
        ConfigureLink(attachedName, clientAddress, prefixLength, mtu, cancellationToken);
        return new LinuxTunDevice(handle, attachedName);
    }

    /// <inheritdoc/>
    public void SendPacket(ReadOnlySpan<byte> ipv6Packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ipv6Packet.Length == 0 || ipv6Packet.Length > Interop.MaxIpPacketSize)
        {
            throw new ArgumentException(
                $"The packet length {ipv6Packet.Length} is outside the TUN range 1..{Interop.MaxIpPacketSize}.");
        }

        Interop.WriteFull(Handle, ipv6Packet);
    }

    /// <inheritdoc/>
    public byte[]? TryReceivePacket(int timeoutMs)
    {
        if (_disposed)
        {
            return null;
        }

        var readable = Interop.PollReadable(Handle, timeoutMs);
        if (!readable)
        {
            return null;
        }

        return Interop.ReadOne(Handle, _receiveBuffer);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Handle.Dispose();
        }
    }

    private static unsafe string AttachInterface(int fd, string requestedName)
    {
        Span<byte> request = stackalloc byte[Interop.InterfaceRequestBytes];
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(requestedName);
        nameBytes.CopyTo(request);
        var flags = Interop.TunMode | Interop.NoPacketInfo;
        request[Interop.InterfaceNameBytes] = (byte)(flags & 0xFF);
        request[Interop.InterfaceNameBytes + 1] = (byte)((flags >> 8) & 0xFF);
        int result;
        fixed (byte* requestPointer = request)
        {
            result = Interop.Ioctl(fd, Interop.TunSetIff, requestPointer);
        }

        if (result < 0)
        {
            throw new IOException(
                $"ioctl(TUNSETIFF, '{requestedName}') failed with errno {Marshal.GetLastWin32Error()}; " +
                "creating a TUN interface requires root privileges.");
        }

        var terminator = request.IndexOf((byte)0);
        var attached = System.Text.Encoding.ASCII.GetString(
            request[..(terminator < 0 ? Interop.InterfaceNameBytes : terminator)]).TrimEnd('\0');
        return attached;
    }

    private static void ConfigureLink(string name, string clientAddress, int prefixLength, int mtu,
        CancellationToken cancellationToken)
    {
        RunIp(cancellationToken, "addr", "add", $"{clientAddress}/{prefixLength}", "dev", name);
        if (mtu > 0)
        {
            RunIp(cancellationToken, "link", "set", "dev", name, "mtu", mtu.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        RunIp(cancellationToken, "link", "set", "dev", name, "up");
    }

    private static void RunIp(CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("ip")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new IOException("The 'ip' command is not available on this system.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"ip {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error.Trim()} {output.Trim()}".Trim());
        }
    }
}

/// <summary>
/// The libc entry points and Linux TUN constants; marshaling is source-generated for NativeAOT and
/// pointer arguments are only used inside unsafe contexts.
/// <para>libc 入口与 Linux TUN 常量；封送由源生成以兼容 NativeAOT，指针实参只在 unsafe 上下文使用。</para>
/// </summary>
internal static unsafe partial class Interop
{
    internal const string TunDevicePath = "/dev/net/tun";
    internal const int OpenReadWrite = 0x2;
    internal const int OpenCloseOnExec = 0x80000;
    internal const uint TunSetIff = 0x400454CA;
    internal const short TunMode = 0x0001;
    internal const short NoPacketInfo = 0x1000;
    internal const int InterfaceNameBytes = 16;
    internal const int InterfaceRequestBytes = 40;
    internal const int Interrupted = 4;
    internal const int Again = 11;
    internal const short PollIn = 0x0001;

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int Open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int Ioctl(int fd, uint request, void* argument);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int Read(int fd, Span<byte> buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int Write(int fd, ReadOnlySpan<byte> buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int Poll(PollDescriptor* descriptors, nuint count, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>
    /// Reads until the whole buffer is consumed, retrying partial writes and EINTR.
    /// <para>循环写入直到全部完成，处理半写与 EINTR。</para>
    /// </summary>
    /// <param name="handle">The TUN file handle. <para>TUN 文件句柄。</para></param>
    /// <param name="packet">The packet bytes. <para>报文字节。</para></param>
    internal static void WriteFull(SafeFileHandle handle, ReadOnlySpan<byte> packet)
    {
        while (!packet.IsEmpty)
        {
            var written = Write((int)handle.DangerousGetHandle(), packet, (nuint)packet.Length);
            if (written < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno == Interop.Interrupted)
                {
                    continue;
                }

                throw new IOException($"write(tun) failed with errno {errno}.");
            }

            packet = packet[written..];
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the TUN fd to become readable.
    /// <para>最多等待 <paramref name="timeoutMs"/> 直到 TUN fd 可读。</para>
    /// </summary>
    /// <param name="handle">The TUN file handle. <para>TUN 文件句柄。</para></param>
    /// <param name="timeoutMs">The wait budget in milliseconds. <para>等待预算（毫秒）。</para></param>
    /// <returns><see langword="true"/> when a packet may be waiting. <para>可能有报文等待时为 <see langword="true"/>。</para></returns>
    internal static bool PollReadable(SafeFileHandle handle, int timeoutMs)
    {
        PollDescriptor descriptor = default;
        descriptor.Fd = (int)handle.DangerousGetHandle();
        descriptor.Events = Interop.PollIn;
        var result = 0;
        do
        {
            result = Poll(&descriptor, 1, timeoutMs);
        }
        while (result < 0 && Marshal.GetLastWin32Error() == Interop.Interrupted);
        if (result < 0)
        {
            throw new IOException($"poll(tun) failed with errno {Marshal.GetLastWin32Error()}.");
        }

        return result > 0 && (descriptor.Revents & Interop.PollIn) != 0;
    }

    /// <summary>
    /// Reads one queued packet into a fresh array; the TUN fd returns at most one packet per read.
    /// <para>读取一个排队报文为新数组；TUN fd 每次读取最多返回一个报文。</para>
    /// </summary>
    /// <param name="handle">The TUN file handle. <para>TUN 文件句柄。</para></param>
    /// <param name="scratch">The reusable read buffer. <para>可复用读取缓冲。</para></param>
    /// <returns>The packet bytes, or <see langword="null"/> when the read reported EAGAIN.
    /// <para>报文字节；读取返回 EAGAIN 时为 <see langword="null"/>。</para></returns>
    internal static byte[]? ReadOne(SafeFileHandle handle, byte[] scratch)
    {
        while (true)
        {
            var fd = (int)handle.DangerousGetHandle();
            var count = Read(fd, scratch, (nuint)scratch.Length);
            if (count > 0)
            {
                return scratch[..count].ToArray();
            }

            if (count == 0)
            {
                throw new IOException("The TUN interface returned end-of-file; the device was closed.");
            }

            var errno = Marshal.GetLastWin32Error();
            if (errno == Interop.Again)
            {
                return null;
            }

            if (errno != Interop.Interrupted)
            {
                throw new IOException($"read(tun) failed with errno {errno}.");
            }
        }
    }
}