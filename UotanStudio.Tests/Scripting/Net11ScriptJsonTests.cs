using System.Text.Json;
using UotanStudio.Scripting;
using UotanStudio.Scripting.Apis;
using Xunit;

namespace UotanStudio.Tests.Scripting;

public sealed class Net11ScriptJsonTests
{
    [Fact]
    public void SimulatedApis_JsonResults_PreserveFieldsEscapingUnicodeAndNullCharacter()
    {
        var fastboot = new SimulatedFastbootScriptApi();
        var image = new SimulatedImageScriptApi();
        var ui = new NoOpUiScriptApi();
        const string path = "C:\\镜像\\boot\0.img";
        const string partition = "boot\"_中文";

        using var flash = Parse(fastboot.Flash(partition, path));
        Assert.True(flash.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(partition, flash.RootElement.GetProperty("partition").GetString());
        Assert.Equal(path, flash.RootElement.GetProperty("imagePath").GetString());
        Assert.Contains("\\u0000", flash.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(":null", flash.RootElement.GetRawText(), StringComparison.Ordinal);

        using var reboot = Parse(fastboot.Reboot("recovery"));
        Assert.True(reboot.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("recovery", reboot.RootElement.GetProperty("mode").GetString());

        using var variable = Parse(fastboot.GetVar("serial\0中文"));
        Assert.Equal("serial\0中文", variable.RootElement.GetProperty("name").GetString());
        Assert.Equal("simulated", variable.RootElement.GetProperty("value").GetString());

        using var slot = Parse(fastboot.SetActiveSlot("a"));
        Assert.True(slot.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("a", slot.RootElement.GetProperty("slot").GetString());

        using var payload = Parse(image.ParsePayload(path));
        Assert.Equal(path, payload.RootElement.GetProperty("path").GetString());
        var payloadPartition = Assert.Single(payload.RootElement.GetProperty("partitions").EnumerateArray());
        Assert.Equal("boot", payloadPartition.GetProperty("name").GetString());
        Assert.Equal(1048576, payloadPartition.GetProperty("size").GetInt64());

        using var super = Parse(image.ParseSuper(path));
        Assert.Equal(path, super.RootElement.GetProperty("path").GetString());
        var superPartition = Assert.Single(super.RootElement.GetProperty("partitions").EnumerateArray());
        Assert.Equal("system", superPartition.GetProperty("name").GetString());
        Assert.Equal(268435456, superPartition.GetProperty("size").GetInt64());

        using var info = Parse(image.GetPartitionInfo(path, partition));
        Assert.Equal(path, info.RootElement.GetProperty("path").GetString());
        Assert.Equal(partition, info.RootElement.GetProperty("partition").GetString());
        Assert.Equal(0, info.RootElement.GetProperty("offset").GetInt64());
        Assert.Equal(1048576, info.RootElement.GetProperty("size").GetInt64());

        using var panel = Parse(ui.ShowPanel("{}"));
        Assert.True(panel.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(0, panel.RootElement.GetProperty("elements").GetInt32());
    }

    [Fact]
    public async Task RunAsync_SimulatedJsonResults_ParsesFieldsThroughStringBridge()
    {
        const string source = """
            const raw = image.parsePayload('/tmp/镜像.img');
            typeof raw + ':' + JSON.parse(raw).partitions[0].name + ':' + JSON.parse(fastboot.getVar('serial')).value;
            """;

        var result = await ScriptRunner.RunAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("string:boot:simulated", result.Output);
    }

    private static JsonDocument Parse(string json)
    {
        var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document;
    }
}