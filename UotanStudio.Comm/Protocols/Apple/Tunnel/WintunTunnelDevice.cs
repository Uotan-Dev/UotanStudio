using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// Windows TUN backend over the Wintun driver (wintun.dll, API 0.14): creates an adapter, configures
/// the negotiated IPv6 address and MTU through the IP Helper API and exchanges raw IPv6 packets
/// through a Wintun session ring. Opening adapters and changing interface addresses requires
/// elevated (administrator) rights; the failure mode is a thrown exception, never a silent no-op.
/// <para>经 Wintun 驱动（wintun.dll，0.14 API）的 Windows TUN 后端：创建适配器、经 IP Helper API
/// 配置协商出的 IPv6 地址与 MTU，并通过 Wintun 会话环交换原始 IPv6 报文。创建适配器与修改接口地址
/// 需要管理员权限；失败一律抛出异常，绝不静默。</para>
/// </summary>
internal sealed class WintunTunnelDevice : ITunDevice
{
    private const uint RingCapacity = 0x400000;
    private const ushort AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint WaitObject0 = 0;

    private readonly Lock _gate = new();
    private IntPtr _adapter;
    private IntPtr _session;
    private IntPtr _readEvent;
    private volatile bool _disposed;

    private WintunTunnelDevice(IntPtr adapter, IntPtr session, IntPtr readEvent, string name)
    {
        _adapter = adapter;
        _session = session;
        _readEvent = readEvent;
        Name = name;
    }

    /// <summary>Gets the adapter name reported to Windows. <para>获取上报给 Windows 的适配器名。</para></summary>
    public string Name { get; }

    /// <summary>
    /// Creates a Wintun adapter, starts its session and configures the IPv6 address and MTU.
    /// <para>创建 Wintun 适配器、启动会话并配置 IPv6 地址与 MTU。</para>
    /// </summary>
    /// <param name="name">The requested adapter name. <para>请求的适配器名。</para></param>
    /// <param name="clientAddress">The tunnel client IPv6 address. <para>隧道本端 IPv6 地址。</para></param>
    /// <param name="prefixLength">The on-link prefix length. <para>链路前缀长度。</para></param>
    /// <param name="mtu">The negotiated interface MTU; zero keeps the adapter default.
    /// <para>协商的接口 MTU；零保持适配器默认。</para></param>
    /// <returns>The opened device. <para>已打开的设备。</para></returns>
    /// <exception cref="IOException">wintun.dll is missing, the adapter cannot be created or the
    /// address/MTU configuration was rejected. <para>wintun.dll 缺失、适配器无法创建或地址/MTU
    /// 配置被拒绝时抛出。</para></exception>
    internal static WintunTunnelDevice Open(string name, string clientAddress, int prefixLength, int mtu)
    {
        if (!WintunNativeLoader.EnsureLoaded())
        {
            throw new IOException(
                "wintun.dll was not found next to the application and no path was registered. " +
                "Download Wintun (https://www.wintun.net/), place wintun.dll next to the executable " +
                "or register its absolute path via AppleTunnel.SetWintunLibraryPath, and run the " +
                "application elevated; creating a Wintun adapter requires administrator rights.");
        }

        var adapter = Interop.WintunCreateAdapter(name, "UotanStudio Tunnel", IntPtr.Zero);
        if (adapter == IntPtr.Zero)
        {
            throw new IOException(
                $"WintunCreateAdapter('{name}') failed (Win32 error {Marshal.GetLastWin32Error()}). " +
                "Run the application elevated and make sure the wintun.dll version matches the installed driver.");
        }

        IntPtr session = IntPtr.Zero;
        try
        {
            Interop.WintunGetAdapterLUID(adapter, out var luid);
            session = Interop.WintunStartSession(adapter, RingCapacity);
            if (session == IntPtr.Zero)
            {
                throw new IOException(
                    $"WintunStartSession failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            ConfigureAdapter(luid, clientAddress, prefixLength, mtu);
            var readEvent = Interop.WintunGetReadWaitEvent(session);
            return new WintunTunnelDevice(adapter, session, readEvent, name);
        }
        catch
        {
            if (session != IntPtr.Zero)
            {
                Interop.WintunEndSession(session);
            }

            Interop.WintunCloseAdapter(adapter);
            throw;
        }
    }

    /// <inheritdoc/>
    public unsafe void SendPacket(ReadOnlySpan<byte> ipv6Packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ipv6Packet.Length == 0 || ipv6Packet.Length > Interop.MaxIpPacketSize)
        {
            throw new ArgumentException(
                $"The packet length {ipv6Packet.Length} is outside the Wintun range 1..{Interop.MaxIpPacketSize}.");
        }

        var session = _session;
        var packet = Interop.WintunAllocateSendPacket(session, (uint)ipv6Packet.Length);
        if (packet == IntPtr.Zero)
        {
            throw new IOException(
                $"WintunAllocateSendPacket failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        ipv6Packet.CopyTo(new Span<byte>((void*)packet, ipv6Packet.Length));
        Interop.WintunSendPacket(session, packet);
    }

    /// <inheritdoc/>
    public byte[]? TryReceivePacket(int timeoutMs)
    {
        if (_disposed)
        {
            return null;
        }

        var session = _session;
        var readEvent = _readEvent;
        var wait = Interop.WaitForSingleObject(readEvent, (uint)Math.Max(0, timeoutMs));
        if (wait != WaitObject0)
        {
            return null;
        }

        var size = 0u;
        var packet = Interop.WintunReceivePacket(session, ref size);
        if (packet == IntPtr.Zero)
        {
            return null;
        }

        var bytes = new byte[size];
        unsafe
        {
            new ReadOnlySpan<byte>((void*)packet, bytes.Length).CopyTo(bytes);
        }

        Interop.WintunReleaseReceivePacket(session, packet);
        return bytes;
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
            if (_session != IntPtr.Zero)
            {
                Interop.WintunEndSession(_session);
                _session = IntPtr.Zero;
            }

            if (_adapter != IntPtr.Zero)
            {
                Interop.WintunCloseAdapter(_adapter);
                _adapter = IntPtr.Zero;
            }

            _readEvent = IntPtr.Zero;
        }
    }

    private static unsafe void ConfigureAdapter(ulong luid, string clientAddress, int prefixLength, int mtu)
    {
        var address = System.Net.IPAddress.Parse(clientAddress);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new IOException(
                $"The tunnel client address {clientAddress} is not an IPv6 address.");
        }

        var row = new Interop.MibUnicastIpAddressRow();
        Interop.InitializeUnicastIpAddressEntry(ref row);
        row.InterfaceLuid = luid;
        row.Address.Family = AfInet6;
        address.GetAddressBytes().CopyTo(new Span<byte>(row.Address.AddressBytes, 16));
        row.OnLinkPrefixLength = (byte)prefixLength;
        row.DadState = Interop.IpDadStatePreferred;
        var created = Interop.CreateUnicastIpAddressEntry(ref row);
        if (created != NoError && created != ErrorObjectAlreadyExists)
        {
            throw new IOException(
                $"CreateUnicastIpAddressEntry({clientAddress}/{prefixLength}) failed (Win32 error {created}). " +
                "Configuring the tunnel adapter address requires administrator rights.");
        }

        if (mtu <= 0)
        {
            return;
        }

        var interfaceRow = new Interop.MibIpInterfaceRow();
        Interop.InitializeIpInterfaceEntry(ref interfaceRow);
        interfaceRow.InterfaceLuid = luid;
        interfaceRow.Family = AfInet6;
        var fetched = Interop.GetIpInterfaceEntry(ref interfaceRow);
        if (fetched != NoError)
        {
            throw new IOException(
                $"GetIpInterfaceEntry failed (Win32 error {fetched}); the negotiated MTU {mtu} was not applied.");
        }

        interfaceRow.NlMtu = (uint)mtu;
        var applied = Interop.SetIpInterfaceEntry(ref interfaceRow);
        if (applied != NoError)
        {
            throw new IOException(
                $"SetIpInterfaceEntry(NlMtu={mtu}) failed (Win32 error {applied}).");
        }
    }
}

/// <summary>
/// Resolves the wintun.dll image: a caller-registered absolute path wins (pre-loaded so the later
/// bare-name import resolves against it), otherwise the DLL next to the application is used.
/// <para>解析 wintun.dll 映像：调用者注册的绝对路径优先（先按路径预加载，使后续裸名导入解析到该映像），
/// 否则使用程序目录下的 DLL。</para>
/// </summary>
internal static class WintunNativeLoader
{
    private static readonly Lock Gate = new();
    private static IntPtr _libraryHandle;

    /// <summary>
    /// Registers the absolute wintun.dll path and pre-loads it. Throws when the file does not exist.
    /// <para>注册 wintun.dll 的绝对路径并预加载。文件不存在时抛出。</para>
    /// </summary>
    /// <param name="libraryPath">The absolute path to wintun.dll. <para>wintun.dll 的绝对路径。</para></param>
    /// <exception cref="ArgumentException">The path is blank. <para>路径为空白时抛出。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist. <para>文件不存在时抛出。</para></exception>
    public static void SetLibraryPath(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            throw new ArgumentException("A non-empty wintun library path is required.", nameof(libraryPath));
        }

        var full = Path.GetFullPath(libraryPath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"wintun.dll was not found at '{full}'.", full);
        }

        lock (Gate)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
            }

            _libraryHandle = NativeLibrary.Load(full);
        }
    }

    /// <summary>
    /// Loads the wintun runtime: the registered path, then <c>wintun.dll</c> resolved through the
    /// standard loader (the application directory included). Returns <see langword="false"/> when
    /// neither is available so the caller can fail loudly.
    /// <para>加载 wintun 运行库：先取注册路径，再经标准加载器（含程序目录）解析 <c>wintun.dll</c>。
    /// 两者都不可用时返回 <see langword="false"/>，让调用方显式失败。</para>
    /// </summary>
    /// <returns><see langword="true"/> when wintun.dll is loaded. <para>wintun.dll 已加载时为 <see langword="true"/>。</para></returns>
    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                return true;
            }

            if (NativeLibrary.TryLoad("wintun.dll", out _libraryHandle))
            {
                return true;
            }

            _libraryHandle = IntPtr.Zero;
            return false;
        }
    }
}

/// <summary>
/// The wintun.dll 0.14 entry points plus the IP Helper (iphlpapi) calls used to configure the
/// adapter address and MTU; all marshaling is source-generated for NativeAOT.
/// <para>wintun.dll 0.14 入口与配置适配器地址/MTU 所需的 IP Helper（iphlpapi）调用；封送全部由
/// 源生成，兼容 NativeAOT。</para>
/// </summary>
internal static unsafe partial class Interop
{
    internal const uint MaxIpPacketSize = 0xFFFF;
    internal const byte IpDadStatePreferred = 4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("wintun.dll", EntryPoint = "WintunCreateAdapter", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);

    [LibraryImport("wintun.dll", EntryPoint = "WintunOpenAdapter", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr WintunOpenAdapter(string name);

    [LibraryImport("wintun.dll", EntryPoint = "WintunDeleteDriver")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WintunDeleteDriver();

    [LibraryImport("wintun.dll", EntryPoint = "WintunCloseAdapter")]
    internal static partial void WintunCloseAdapter(IntPtr adapter);

    [LibraryImport("wintun.dll", EntryPoint = "WintunGetAdapterLUID")]
    internal static partial void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);

    [LibraryImport("wintun.dll", EntryPoint = "WintunStartSession")]
    internal static partial IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [LibraryImport("wintun.dll", EntryPoint = "WintunEndSession")]
    internal static partial void WintunEndSession(IntPtr session);

    [LibraryImport("wintun.dll", EntryPoint = "WintunGetReadWaitEvent")]
    internal static partial IntPtr WintunGetReadWaitEvent(IntPtr session);

    [LibraryImport("wintun.dll", EntryPoint = "WintunReceivePacket")]
    internal static partial IntPtr WintunReceivePacket(IntPtr session, ref uint packetSize);

    [LibraryImport("wintun.dll", EntryPoint = "WintunReleaseReceivePacket")]
    internal static partial void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [LibraryImport("wintun.dll", EntryPoint = "WintunAllocateSendPacket")]
    internal static partial IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [LibraryImport("wintun.dll", EntryPoint = "WintunSendPacket")]
    internal static partial void WintunSendPacket(IntPtr session, IntPtr packet);

    [LibraryImport("iphlpapi.dll", EntryPoint = "InitializeUnicastIpAddressEntry")]
    internal static partial void InitializeUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [LibraryImport("iphlpapi.dll", EntryPoint = "CreateUnicastIpAddressEntry")]
    internal static partial uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [LibraryImport("iphlpapi.dll", EntryPoint = "InitializeIpInterfaceEntry")]
    internal static partial void InitializeIpInterfaceEntry(ref MibIpInterfaceRow row);

    [LibraryImport("iphlpapi.dll", EntryPoint = "GetIpInterfaceEntry")]
    internal static partial uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [LibraryImport("iphlpapi.dll", EntryPoint = "SetIpInterfaceEntry")]
    internal static partial uint SetIpInterfaceEntry(ref MibIpInterfaceRow row);

    /// <summary>SOCKADDR_INET as used for IPv6 unicast address rows. <para>IPv6 单播地址行使用的 SOCKADDR_INET。</para></summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct MibSockaddrInet
    {
        [FieldOffset(0)]
        public ushort Family;

        [FieldOffset(2)]
        public ushort Port;

        [FieldOffset(4)]
        public uint FlowInfo;

        [FieldOffset(8)]
        public fixed byte AddressBytes[16];

        [FieldOffset(24)]
        public uint ScopeId;
    }

    /// <summary>
    /// MIB_UNICASTIPADDRESS_ROW (netioapi.h, x64 layout: Address 28, InterfaceLuid 8, InterfaceIndex,
    /// origins, lifetimes, prefix length, SkipAsSource, DadState, ScopeId, CreationTimeStamp).
    /// <para>MIB_UNICASTIPADDRESS_ROW（netioapi.h 的 x64 布局）。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUnicastIpAddressRow
    {
        public MibSockaddrInet Address;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint PrefixOrigin;
        public uint SuffixOrigin;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public uint DadState;
        public uint ScopeId;
        public long CreationTimeStamp;
    }

    /// <summary>
    /// MIB_IPINTERFACE_ROW (netioapi.h, x64 layout including the 16-entry ZoneIndices array).
    /// <para>MIB_IPINTERFACE_ROW（netioapi.h 的 x64 布局，含 16 项 ZoneIndices）。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpInterfaceRow
    {
        public ushort Family;
        public uint InterfaceIndex;
        public ulong InterfaceLuid;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public uint RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public uint LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        public fixed uint ZoneIndices[16];
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public uint TransmitOffload;
        public uint ReceiveOffload;
        public byte DisableDefaultRoutes;
    }
}