using System.Text.Json.Serialization;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Mcp.Tools;

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

/// <summary>
/// <para>清单中单个 USB 端点的负载。<c>Protocol</c> 来自 Comm 的协议分类器，无法分类时为
/// <c>unknown</c>。<c>DevicePath</c> 与 <c>DeviceKey</c> 仅在请求详细清单时才填充，默认结果省略它们以保持精简。</para>
/// One USB endpoint in the inventory. <c>Protocol</c> comes from the communication layer's protocol
/// classifier and is <c>unknown</c> when nothing identifies a known protocol. <c>DevicePath</c> and
/// <c>DeviceKey</c> are populated only for a detailed inventory request, so the default payload stays small.
/// </summary>
internal sealed record UsbDevicePayload(
    string Api,
    string SourceApiKind,
    int VendorId,
    int ProductId,
    string? Serial,
    string Protocol,
    int? InterfaceClass,
    int? InterfaceSubClass,
    int? InterfaceProtocol,
    string? DevicePath = null,
    string? DeviceKey = null);

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
[JsonSerializable(typeof(OtaZipVerificationPayload))]
[JsonSerializable(typeof(OtaZipPatchPayload))]
[JsonSerializable(typeof(OtaZipRebuildPayload))]
[JsonSerializable(typeof(SuperPayloadConversionPayload))]
[JsonSerializable(typeof(PreparedToolsPayload))]
[JsonSerializable(typeof(EnvPayload))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class McpToolJsonContext : JsonSerializerContext;