using System.Diagnostics;
using Xunit;

namespace UotanStudio.Tests.Comm.TestDoubles;

/// <summary>Test-owned Linux pseudo-terminal; never enumerates or opens a physical serial device.</summary>
internal sealed class SerialPty(Process process, CancellationTokenSource timeout) : IAsyncDisposable
{
    internal string SlavePath { get; private set; } = "";
    internal CancellationToken Token => timeout.Token;

    internal static async Task<SerialPty> CreateAsync()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"),
            "Requires Linux and /usr/bin/python3 to create an isolated pseudo-terminal.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo("/usr/bin/python3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            import errno, fcntl, os, pty, select, sys, termios, tty
            master, slave = pty.openpty()
            try:
                fcntl.ioctl(slave, termios.TIOCEXCL)
                fcntl.ioctl(slave, termios.TIOCNXCL)
            except OSError as error:
                os.close(master)
                os.close(slave)
                if error.errno in (errno.ENOTTY, errno.ENOSYS, errno.EOPNOTSUPP):
                    print("UNSUPPORTED_EXCLUSIVE", flush=True)
                    sys.exit(0)
                raise
            tty.setraw(slave)
            print(os.ttyname(slave), flush=True)
            try:
                for line in sys.stdin:
                    command, _, argument = line.strip().partition(" ")
                    if command == "WRITE":
                        data = bytes.fromhex(argument)
                        while data:
                            count = os.write(master, data)
                            data = data[count:]
                        print("OK", flush=True)
                    elif command == "READ":
                        remaining = int(argument)
                        output = bytearray()
                        while remaining:
                            if not select.select([master], [], [], 3)[0]:
                                raise TimeoutError("Pseudo-terminal read timeout")
                            data = os.read(master, remaining)
                            if not data:
                                raise EOFError("Pseudo-terminal closed")
                            output.extend(data)
                            remaining -= len(data)
                        print(output.hex(), flush=True)
                    elif command == "POLL":
                        print("READY" if select.select([master], [], [], 0)[0] else "EMPTY", flush=True)
                    else:
                        raise ValueError("Unknown test command")
            finally:
                os.close(master)
                os.close(slave)
            """);
        Process? process = null;
        SerialPty? fixture = null;
        try
        {
            process = Process.Start(start) ?? throw new IOException("Could not start pseudo-terminal helper.");
            fixture = new SerialPty(process, timeout);
            fixture.SlavePath = await process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new IOException("Pseudo-terminal helper did not return a slave path.");
            Assert.SkipWhen(fixture.SlavePath == "UNSUPPORTED_EXCLUSIVE",
                "The kernel pseudo-terminal does not support the exclusive-access ioctl required by System.IO.Ports.");
            Assert.StartsWith("/dev/pts/", fixture.SlavePath, StringComparison.Ordinal);
            return fixture;
        }
        catch
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
            else
            {
                process?.Dispose();
                timeout.Dispose();
            }
            throw;
        }
    }

    internal async Task WriteAsync(byte[] bytes) =>
        Assert.Equal("OK", await CommandAsync("WRITE " + Convert.ToHexString(bytes)));

    internal async Task<byte[]> ReadAsync(int count) => Convert.FromHexString(await CommandAsync($"READ {count}"));

    internal async Task<bool> HasPendingOutputAsync() => await CommandAsync("POLL") == "READY";

    private async Task<string> CommandAsync(string command)
    {
        await process.StandardInput.WriteLineAsync(command.AsMemory(), Token);
        await process.StandardInput.FlushAsync(Token);
        return await process.StandardOutput.ReadLineAsync(Token) ?? throw new IOException("Pseudo-terminal helper exited.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            process.Dispose();
            timeout.Dispose();
        }
    }
}