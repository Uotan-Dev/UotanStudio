// NOTE: This compact port of the update_metadata.proto messages does not carry a
// protobuf reflection descriptor. The message classes implement Google.Protobuf's
// IMessage<T> contract and are fully functional for parsing (MessageParser<T>) and
// serialization (CodedOutputStream) of real Android OTA manifests; the CLR-to-descriptor
// binding and descriptor-based JSON formatting (JsonFormatter) are intentionally omitted
// to keep the port minimal.
//
// 说明：本精简移植版未携带 update_metadata.proto 的反射描述符。消息类实现了
// Google.Protobuf 的 IMessage<T> 契约，可完整用于真实 Android OTA 清单的解析
// （MessageParser<T>）与序列化（CodedOutputStream）；为保持移植精简，有意省略了
// CLR-描述符绑定与基于描述符的 JSON 格式化（JsonFormatter）。
namespace UotanStudio.Firmware.Payload.Proto;