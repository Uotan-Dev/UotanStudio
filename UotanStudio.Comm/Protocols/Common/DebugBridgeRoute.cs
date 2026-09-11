using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Comm.Protocols.Common;

/// <summary>Resolves debug bridge ownership before any USB handle is opened. Only a refused connection means absent.</summary>
public static class DebugBridgeRoute
{
    /// <summary>Checks an endpoint without caching absence or converting protocol failures into USB permission.</summary>
    public static bool IsListening(string host, int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
    }

    /// <summary>Chooses a server even when the requested target is missing or unauthorized; those errors must not open USB.</summary>
    public static bool UseAdbServer() => IsListening("127.0.0.1", 5037);

    /// <summary>Checks the configured HDC endpoint on every new operation.</summary>
    public static bool UseHdcServer(string host = "127.0.0.1", int? port = null)
        => IsListening(host, port ?? HdcServerClient.DefaultServerPort);

    /// <summary>Rejects raw debug USB access while a server owns the protocol.</summary>
    public static void RequireDirectUsb(byte? interfaceClass, byte? subclass, byte? protocol)
    {
        if (interfaceClass != 0xff || protocol != 1) return;
        bool present = subclass switch { 0x42 => UseAdbServer(), 0x50 => UseHdcServer(), _ => false };
        if (present) throw new IOException("A debug bridge server is running; use its target service instead of opening USB.");
    }

    /// <summary>Creates a routed ADB session; the direct factory is invoked only when the server is absent.</summary>
    public static AdbSession OpenAdb(string serial, Func<AdbSession> openDirect,
        string host = "127.0.0.1", int port = 5037)
        => IsListening(host, port) ? new AdbSession(new AdbServerServices(serial, host, port)) : openDirect();

    /// <summary>Creates a routed HDC session without relying on a discovery key prefix.</summary>
    public static HdcSession OpenHdc(string serial, Func<HdcSession> openDirect,
        string host = "127.0.0.1", int? port = null)
    {
        if (!UseHdcServer(host, port)) return openDirect();
        using var server = new HdcServerClient(host, port);
        return server.CreateSession(serial);
    }
}
