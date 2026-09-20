using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Mcp.Tools;

// Tool result payloads are concrete records so the MCP surface stays NativeAOT-safe:
// every value crossing ToolResult.Data is serialized through the source-generated
// context below instead of the reflection resolver.
// <para>工具结果负载是具体记录类型，以保证 MCP 面的 NativeAOT 兼容性：
// 经 ToolResult.Data 传出的每个值都走下方 source-gen 上下文，而非反射解析器。</para>

internal sealed record CompressionAlgorithmPayload(
    string Format,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Extensions,
    bool CanCompress,
    bool CanDecompress,
    bool IsStreaming);

internal sealed record CompressionIoPayload(
    string Input,
    string Output,
    long InputBytes,
    long OutputBytes);

internal sealed record DeliveryOutputsPayload(
    IReadOnlyList<string> Planned,
    IReadOnlyList<string> Existing);

internal sealed record UsbDevicePayload(
    string Api,
    string SourceApiKind,
    int VendorId,
    int ProductId,
    string? Serial,
    string DevicePath,
    string DeviceKey,
    int? InterfaceClass,
    int? InterfaceSubClass,
    int? InterfaceProtocol);

internal sealed record AdbTargetPayload(string Serial, string State);

internal sealed record AdbShellPayload(string Serial, string Command, string Output);

internal sealed record AdbFileSyncPayload(
    string Serial,
    string RemotePath,
    string Local,
    long Bytes);

internal sealed record AdbRebootPayload(string Serial, string? Mode);

internal sealed record FlashPayload(string Partition, string Image);

internal sealed record PartitionPayload(string Partition);

internal sealed record FormatListEntryPayload(
    string Name,
    IReadOnlyList<string> Extensions,
    bool CanPack);

internal sealed record DetectedFormatPayload(
    string Format,
    IReadOnlyList<string> Extensions,
    bool CanPack);

internal sealed record FirmwareData(
    IReadOnlyList<string> Entries,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<FirmwareComponent> Components);

internal sealed record PathTextPayload(string Path, string Text);

internal sealed record PathXmlPayload(string Path, string Xml);

internal sealed record OutputBytesPayload(string Output, long Bytes);

internal sealed record DexInfoPayload(
    string Path,
    int Strings,
    int Types,
    int Fields,
    int Methods,
    IReadOnlyList<string> Classes);

internal sealed record ArscPackagePayload(string Name, int PackageId, int TypeCount);

internal sealed record ArscSummaryPayload(string Path, IReadOnlyList<ArscPackagePayload> Packages);

internal sealed record InputOutputPayload(string Input, string Output);

internal sealed record PathPayload(string Path);

internal sealed record ContainerExtractionPayload(
    string Kind,
    IReadOnlyList<string> Entries,
    IReadOnlyList<string> OutputPaths);

internal sealed record EnvPayload(
    string Name,
    string Version,
    string? Root,
    bool Unrestricted,
    bool AllowFlash,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string FrameworkDescription,
    int FormatHandlerCount,
    IReadOnlyList<string> WritableFormats,
    IReadOnlyList<string> CompressionFormats);

/// <summary>
/// Source-generated JSON types for every tool payload crossing <see cref="ToolResult.Data"/>.
/// <para><see cref="ToolResult.Data"/> 上所有工具负载的 source-gen JSON 类型。</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(CompressionAlgorithmPayload))]
[JsonSerializable(typeof(List<CompressionAlgorithmPayload>))]
[JsonSerializable(typeof(CompressionIoPayload))]
[JsonSerializable(typeof(DeliveryOutputsPayload))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(UsbDevicePayload))]
[JsonSerializable(typeof(List<UsbDevicePayload>))]
[JsonSerializable(typeof(AdbTargetPayload))]
[JsonSerializable(typeof(List<AdbTargetPayload>))]
[JsonSerializable(typeof(AdbShellPayload))]
[JsonSerializable(typeof(AdbFileSyncPayload))]
[JsonSerializable(typeof(AdbRebootPayload))]
[JsonSerializable(typeof(FlashPayload))]
[JsonSerializable(typeof(PartitionPayload))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(FormatListEntryPayload))]
[JsonSerializable(typeof(List<FormatListEntryPayload>))]
[JsonSerializable(typeof(DetectedFormatPayload))]
[JsonSerializable(typeof(FirmwareData))]
[JsonSerializable(typeof(PathTextPayload))]
[JsonSerializable(typeof(PathXmlPayload))]
[JsonSerializable(typeof(OutputBytesPayload))]
[JsonSerializable(typeof(DexInfoPayload))]
[JsonSerializable(typeof(ArscPackagePayload))]
[JsonSerializable(typeof(List<ArscPackagePayload>))]
[JsonSerializable(typeof(ArscSummaryPayload))]
[JsonSerializable(typeof(InputOutputPayload))]
[JsonSerializable(typeof(PathPayload))]
[JsonSerializable(typeof(ContainerExtractionPayload))]
[JsonSerializable(typeof(EnvPayload))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class McpToolJsonContext : JsonSerializerContext;