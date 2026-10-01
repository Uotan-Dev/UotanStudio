using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class McpToolPayloadRegistrationTests
{
    private static readonly JsonSerializerOptions ProbeOptions = new()
    {
        TypeInfoResolver = McpToolJsonContext.Default
    };

    [Fact]
    public void ToolPayloadTypes_EveryPayloadRecord_HasGeneratedMetadata()
    {
        var payloadTypes = typeof(ToolResult).Assembly.DefinedTypes
            .Where(type => type.IsSealed && !type.IsNested
                && type.Name.EndsWith("Payload", StringComparison.Ordinal))
            .Select(type => type.AsType())
            .ToList();

        Assert.True(payloadTypes.Count > 20,
            $"Expected the whole tool payload set, found {payloadTypes.Count} record(s).");

        foreach (var type in payloadTypes)
        {
            var info = ((IJsonTypeInfoResolver)McpToolJsonContext.Default).GetTypeInfo(type, ProbeOptions);
            Assert.NotNull(info);
            Assert.Equal(type, info!.Type);
        }
    }

    [Theory]
    [InlineData(typeof(OtaZipVerificationPayload))]
    [InlineData(typeof(OtaZipPatchPayload))]
    [InlineData(typeof(OtaZipRebuildPayload))]
    [InlineData(typeof(SuperPayloadConversionPayload))]
    [InlineData(typeof(PreparedToolsPayload))]
    [InlineData(typeof(UsbDevicePayload))]
    [InlineData(typeof(ContainerExtractionPayload))]
    [InlineData(typeof(EnvPayload))]
    [InlineData(typeof(FirmwareData))]
    [InlineData(typeof(ToolResult))]
    public void PayloadRecord_TypeResolvesThroughSourceGeneratedContext(Type payloadType)
    {
        var info = ((IJsonTypeInfoResolver)McpToolJsonContext.Default).GetTypeInfo(payloadType, ProbeOptions);

        Assert.NotNull(info);
        Assert.Equal(payloadType, info!.Type);
    }

    [Fact]
    public void UnregisteredRecord_IsNotResolvable_ProvingTheGateCanFail()
    {
        var info = ((IJsonTypeInfoResolver)McpToolJsonContext.Default)
            .GetTypeInfo(typeof(UnregisteredProbeRecord), ProbeOptions);

        Assert.Null(info);
    }

    private sealed record UnregisteredProbeRecord(string Value);
}