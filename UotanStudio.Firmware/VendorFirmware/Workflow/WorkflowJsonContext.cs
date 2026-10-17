using System.Text.Json.Serialization;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.VendorFirmware.Workflow;

/// <summary>
/// Source-generated serializer for workflow metadata written without indentation.
/// <para>用于工作流元数据的源生成序列化器，输出不带缩进。</para>
/// </summary>
/// <remarks>
/// The options deliberately mirror the compact form produced by <c>JsonNode.ToJsonString()</c> with camel-cased
/// property names, so a streaming digest of this output matches the digest recorded by the preservation manifest.
/// <para>这些选项刻意对齐 <c>JsonNode.ToJsonString()</c> 生成的紧凑形式并采用驼峰属性名，使本输出的流式摘要与保留清单记录的摘要一致。</para>
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepackMetadata))]
internal partial class WorkflowJsonContext : JsonSerializerContext;