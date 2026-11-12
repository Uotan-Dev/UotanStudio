using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class EdlSessionStorageMediaTests
{
    private static byte[] Document(string body) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n{body}\n</data>\n");

    private static FirehoseConfiguration Configuration() => new()
    {
        AutoDetectSectorSize = false,
        SectorSize = 4096,
        ResponseTimeoutSeconds = 1,
        ConfigureRetryIntervalMs = 1,
        ConfigureResponseTimeoutMs = 500,
        ConfigureFirstAttemptTimeoutMs = 500
    };

    private static ScriptedCommTransport CreateTransport()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Document("<response value=\"ACK\" />"));
        transport.OnWrite = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            return text.Contains("<configure", StringComparison.Ordinal)
                ?
                [
                    Document("<log value=\"Storage type set to value\" />"),
                    Document($"<response value=\"ACK\" MemoryName=\"{RequestedMemoryName(text)}\" " +
                        "MaxPayloadSizeToTargetInBytes=\"1048576\" " +
                        "MaxPayloadSizeToTargetInBytesSupported=\"1048576\" MaxPayloadSizeFromTargetInBytes=\"8192\" />")
                ]
                : [Document("<response value=\"ACK\" />")];
        };
        return transport;
    }

    private static string RequestedMemoryName(string configure)
    {
        const string Marker = "MemoryName=\"";
        var start = configure.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        return configure[start..configure.IndexOf('"', start)];
    }

    private static string ConfigureText(ScriptedCommTransport transport) =>
        transport.Writes
            .Select(Encoding.UTF8.GetString)
            .Single(text => text.Contains("<configure", StringComparison.Ordinal));

    [Fact]
    public async Task Initialize_DeclaredUfsMedia_SendsUfsMemoryName()
    {
        var transport = CreateTransport();
        using var session = new EdlSession(transport, Configuration());
        var options = new EdlInitializationOptions { StorageType = FirehoseStorageType.Ufs };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(FirehoseStorageType.Ufs, session.Firehose!.Configuration.StorageType);
        Assert.Contains("MemoryName=\"ufs\"", ConfigureText(transport), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_ConfiguredSessionAskedForOtherMedia_ReconfiguresWithIt()
    {
        var transport = CreateTransport();
        using var session = new EdlSession(transport, Configuration());
        await session.InitializeAsync(null, new EdlInitializationOptions { StorageType = FirehoseStorageType.Emmc },
            TestContext.Current.CancellationToken);

        await session.InitializeAsync(null, new EdlInitializationOptions { StorageType = FirehoseStorageType.Ufs },
            TestContext.Current.CancellationToken);

        var configures = transport.Writes
            .Select(Encoding.UTF8.GetString)
            .Where(text => text.Contains("<configure", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, configures.Length);
        Assert.Contains("MemoryName=\"eMMC\"", configures[0], StringComparison.Ordinal);
        Assert.Contains("MemoryName=\"ufs\"", configures[1], StringComparison.Ordinal);
        Assert.Equal(FirehoseStorageType.Ufs, session.Firehose!.Configuration.StorageType);
    }

    [Fact]
    public async Task Initialize_WithoutMediaChoice_ConfiguresUfs()
    {
        var transport = CreateTransport();
        using var session = new EdlSession(transport, Configuration());

        await session.InitializeAsync(null, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(FirehoseStorageType.Ufs, session.Firehose!.Configuration.StorageType);
        Assert.Contains("MemoryName=\"ufs\"", ConfigureText(transport), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_ProgrammerRefusesUfs_FallsBackToEmmc()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Document("<response value=\"ACK\" />"));
        transport.OnWrite = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.Contains("<configure", StringComparison.Ordinal))
            {
                return [Document("<response value=\"ACK\" />")];
            }

            return text.Contains("MemoryName=\"ufs\"", StringComparison.Ordinal)
                ?
                [
                    Document("<log value=\"ERROR: Not support configure MemoryName UFS\" />"),
                    Document("<response value=\"NAK\" />")
                ]
                :
                [
                    Document("<response value=\"ACK\" MemoryName=\"eMMC\" " +
                        "MaxPayloadSizeToTargetInBytes=\"1048576\" MaxPayloadSizeFromTargetInBytes=\"8192\" />")
                ];
        };
        using var session = new EdlSession(transport, Configuration());

        await session.InitializeAsync(null, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(FirehoseStorageType.Emmc, session.Firehose!.Configuration.StorageType);
        var configures = transport.Writes
            .Select(Encoding.UTF8.GetString)
            .Where(text => text.Contains("<configure", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, configures.Length);
        Assert.Contains("MemoryName=\"ufs\"", configures[0], StringComparison.Ordinal);
        Assert.Contains("MemoryName=\"eMMC\"", configures[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_SameDeclaredMediaTwice_ConfiguresOnce()
    {
        var transport = CreateTransport();
        using var session = new EdlSession(transport, Configuration());
        var options = new EdlInitializationOptions { StorageType = FirehoseStorageType.Ufs };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);
        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.Single(transport.Writes,
            bytes => Encoding.UTF8.GetString(bytes).Contains("<configure", StringComparison.Ordinal));
    }
}