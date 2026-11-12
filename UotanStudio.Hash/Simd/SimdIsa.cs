namespace UotanStudio.Hash.Simd;

/// <summary>
/// Identifies the processor instruction families that the byte-block kernels can use, and the scalar fallback that is
/// always available.
/// <para>标识字节块内核可使用的处理器指令族，以及始终可用的标量软件回退。</para>
/// </summary>
/// <remarks>
/// The flags describe the processor, not a single call: <see cref="SimdCapabilities.SupportedIsas"/> reports every
/// family detected on the current machine while <see cref="SimdCapabilities.ActiveIsa"/> names the one kernel chosen
/// for block operations. A flag therefore does not guarantee that a particular algorithm has a dedicated path for it.
/// <para>这些标志描述处理器而非单次调用：<see cref="SimdCapabilities.SupportedIsas"/> 报告本机检测到的全部指令族，
/// <see cref="SimdCapabilities.ActiveIsa"/> 指出块运算实际选用的内核。因此某个标志不代表每种算法都为该指令族写了专属路径。</para>
/// </remarks>
[Flags]
public enum SimdIsa
{
    /// <summary>No vector instruction family was detected.<para>未检测到任何向量指令族。</para></summary>
    None = 0,

    /// <summary>The portable byte-at-a-time loop used when hardware acceleration is unavailable or disabled.
    /// <para>硬件加速不可用或被禁用时使用的可移植逐字节循环。</para></summary>
    Scalar = 1 << 0,

    /// <summary>Width-agnostic <c>System.Numerics.Vector&lt;T&gt;</c> operations lowered by the JIT to the widest
    /// available registers, including RISC-V hosts with a vector-capable runtime.
    /// <para>由 JIT 降级到最宽可用寄存器的 <c>System.Numerics.Vector&lt;T&gt;</c> 运算，包含带向量运行时的 RISC-V 主机。</para></summary>
    PortableVectors = 1 << 1,

    /// <summary>x86 SSE2 and SSSE3 128-bit integer operations.<para>x86 SSE2 与 SSSE3 的 128 位整数运算。</para></summary>
    Sse2 = 1 << 2,

    /// <summary>x86 AVX and AVX2 256-bit integer operations.<para>x86 AVX 与 AVX2 的 256 位整数运算。</para></summary>
    Avx2 = 1 << 3,

    /// <summary>x86 AVX-512F and AVX-512BW 512-bit integer operations.<para>x86 AVX-512F 与 AVX-512BW 的 512 位整数运算。</para></summary>
    Avx512 = 1 << 4,

    /// <summary>x86 AVX10.1 and AVX10.2 converged vector ISA, including the 512-bit form exposed through
    /// <c>Avx10v1.V512</c> and <c>Avx10v2.V512</c>.
    /// <para>x86 AVX10.1 与 AVX10.2 融合向量指令集，包含由 <c>Avx10v1.V512</c> 与 <c>Avx10v2.V512</c> 暴露的 512 位形态。</para></summary>
    Avx10 = 1 << 5,

    /// <summary>ARM64 Advanced SIMD 128-bit operations.<para>ARM64 Advanced SIMD 的 128 位运算。</para></summary>
    AdvSimd = 1 << 6,

    /// <summary>ARM64 CRC32 and CRC32C instructions.<para>ARM64 CRC32 与 CRC32C 指令。</para></summary>
    ArmCrc = 1 << 7,

    /// <summary>ARM64 AES, PMULL, SHA-1 and SHA-256 cryptographic extensions used by the runtime hashing primitives.
    /// <para>运行时摘要原语使用的 ARM64 AES、PMULL、SHA-1 与 SHA-256 密码学扩展。</para></summary>
    ArmCrypto = 1 << 8,

    /// <summary>ARM64 Scalable Vector Extension with runtime-determined vector width.
    /// <para>向量宽度由运行时决定的 ARM64 可伸缩向量扩展。</para></summary>
    Sve = 1 << 9,

    /// <summary>ARM64 Scalable Vector Extension version 2.<para>ARM64 可伸缩向量扩展第二版。</para></summary>
    Sve2 = 1 << 10,
}