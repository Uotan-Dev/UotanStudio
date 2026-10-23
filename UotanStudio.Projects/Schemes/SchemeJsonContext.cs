using System.Text.Json.Serialization;

namespace UotanStudio.Projects.Schemes;

/// <summary>
/// Scheme package JSON serialization context (AOT source-generated).
/// <para>Scheme 软件包 JSON 序列化上下文（AOT 源生成）。</para>
/// </summary>
[JsonSerializable(typeof(SchemePackage))]
[JsonSerializable(typeof(SchemeStage))]
[JsonSerializable(typeof(List<SchemeStage>))]
[JsonSerializable(typeof(List<SchemeInputDefinition>))]
[JsonSerializable(typeof(List<SchemeAsset>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SchemeJsonContext : JsonSerializerContext;