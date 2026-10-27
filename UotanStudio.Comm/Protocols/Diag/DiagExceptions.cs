namespace UotanStudio.Comm.Protocols.Diag;

/// <summary>
/// Base type for DIAG protocol failures thrown by this library.
/// <para>本库抛出的 DIAG 协议失败基类型。</para>
/// </summary>
public class DiagException(string message) : Exception(message)
{
}

/// <summary>
/// Thrown when a DIAG reply frame is malformed or fails its checksum.
/// <para>当 DIAG 应答帧畸形或校验失败时抛出。</para>
/// </summary>
public sealed class InvalidDiagFrameException(string message) : DiagException(message)
{
}