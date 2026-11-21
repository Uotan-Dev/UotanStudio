using System.Runtime.InteropServices;

namespace UotanStudio.Hash.Simd;

/// <summary>
/// Publishes the hand-written LoongArch64 LSX/LASX and RiscV64 RVV machine-code kernels into executable memory and
/// exposes them as function pointers; the sources live in <c>Simd/Native/loongarch64.S</c> and
/// <c>Simd/Native/riscv64.S</c>.
/// <para>把手写的 LoongArch64 LSX/LASX 与 RiscV64 RVV 机器码内核发布到可执行内存并以函数指针暴露；
/// 汇编源位于 <c>Simd/Native/loongarch64.S</c> 与 <c>Simd/Native/riscv64.S</c>。</para>
/// </summary>
/// <remarks>
/// .NET has no JIT vector backend on LoongArch64 and RiscV64, so the managed intrinsics fall back to scalar loops
/// there. These kernels close that gap by executing real vector instructions assembled from the <c>Simd/Native</c>
/// sources: the byte arrays below are the exact <c>.text</c> of those files (validated byte-identical across GNU as
/// builds on the target hosts and cross toolchains), published through <see cref="ExecutableCodePage"/> and invoked
/// via blittable function pointers. Every routine is a leaf function with no literal pools or absolute addresses, so
/// the bytes are position independent. Activation runs once: hardware capabilities are read via
/// <see cref="NativeCpuFeatures"/>, the kernels are emitted, and a deterministic self-test compares each native
/// kernel against a local scalar oracle; any failure leaves the pointers unset and dispatch stays on the scalar
/// software kernels. The LoongArch kernels require naturally aligned pointers and whole-block lengths, which the
/// managed callers guarantee; the RISC-V kernels accept any length and element-aligned pointers.
/// <para>.NET 在 LoongArch64 与 RiscV64 上没有 JIT 向量后端，托管 intrinsic 在那里退化为标量循环。这些内核通过
/// 执行由 <c>Simd/Native</c> 源汇编出的真实向量指令补上缺口：下方字节数组是这些汇编文件的确切 <c>.text</c>
/// （已在目标主机与交叉工具链上验证逐字节一致），经 <see cref="ExecutableCodePage"/> 发布并通过无混编函数指针调用。
/// 每个例程都是无字面池、无绝对地址的叶子函数，字节与位置无关。激活只执行一次：经 <see cref="NativeCpuFeatures"/>
/// 读取硬件能力、发射内核，再以确定性自测把每个原生内核与本地标量预言比对；任何失败都保持指针为空，分派继续走
/// 标量软件内核。LoongArch 内核要求自然对齐的指针与整块长度，由托管调用方保证；RISC-V 内核接受任意长度与
/// 元素对齐的指针。</para>
/// </remarks>
internal static unsafe class NativeVectorKernels
{
    private const int RiscVXorOffset = 0;
    private const int RiscVXorConstantOffset = 32;
    private const int RiscVSumOffset = 66;
    private const int RiscVXorFoldOffset = 136;

    private const int LoongArchXor256Offset = 0;
    private const int LoongArchXorConstant256Offset = 44;
    private const int LoongArchSum256Offset = 84;
    private const int LoongArchXorFold256Offset = 304;
    private const int LoongArchXor128Offset = 496;
    private const int LoongArchXorConstant128Offset = 540;
    private const int LoongArchSum128Offset = 580;
    private const int LoongArchXorFold128Offset = 736;

    /// <summary>The <c>.text</c> of <c>Simd/Native/riscv64.S</c> up to the last production kernel.
    /// <para><c>Simd/Native/riscv64.S</c> 到最后一个生产内核为止的 <c>.text</c>。</para></summary>
    private static readonly byte[] RiscVMachineCode =
    [
        0xd7, 0x72, 0x36, 0x0c, 0x07, 0x00, 0x05, 0x02, 0x07, 0x84, 0x05, 0x02, 0x57, 0x00, 0x04, 0x2e,
        0x27, 0x00, 0x05, 0x02, 0x16, 0x95, 0x96, 0x95, 0x33, 0x06, 0x56, 0x40, 0x75, 0xf2, 0x82, 0x80,
        0xd7, 0x7e, 0x30, 0x0c, 0x57, 0xcc, 0x05, 0x5e, 0xd7, 0x72, 0x36, 0x0c, 0x07, 0x00, 0x05, 0x02,
        0x57, 0x00, 0x0c, 0x2e, 0x27, 0x00, 0x05, 0x02, 0x16, 0x95, 0x33, 0x06, 0x56, 0x40, 0x6d, 0xf6,
        0x82, 0x80, 0xd7, 0x7e, 0x30, 0x0d, 0x57, 0x38, 0x00, 0x5e, 0xd7, 0xf2, 0x15, 0x0c, 0x07, 0x00,
        0x05, 0x02, 0x57, 0x32, 0x00, 0x5e, 0x57, 0x24, 0x02, 0xc2, 0xd7, 0xfe, 0xa2, 0x0c, 0x57, 0x36,
        0x00, 0x5e, 0x57, 0x2c, 0x86, 0xc2, 0xd7, 0xfe, 0x32, 0x0d, 0x57, 0x08, 0x0c, 0x03, 0x16, 0x95,
        0xb3, 0x85, 0x55, 0x40, 0xf9, 0xf9, 0xd7, 0x7e, 0x30, 0x0d, 0x57, 0x3c, 0x00, 0x5e, 0x57, 0x20,
        0x0c, 0x03, 0x57, 0x25, 0x00, 0x42, 0x82, 0x80, 0xd7, 0x7e, 0xb0, 0x0c, 0x57, 0x38, 0x00, 0x5e,
        0xd7, 0xf2, 0xb5, 0x0c, 0x07, 0x50, 0x05, 0x02, 0x57, 0x08, 0x00, 0x2f, 0x13, 0x9e, 0x12, 0x00,
        0x72, 0x95, 0xb3, 0x85, 0x55, 0x40, 0xed, 0xf5, 0xd7, 0x7e, 0x80, 0x0c, 0x57, 0x3c, 0x00, 0x5e,
        0xd7, 0x7e, 0xb0, 0x0c, 0x57, 0x20, 0x0c, 0x0f, 0x57, 0x25, 0x00, 0x42, 0x82, 0x80,
    ];

    /// <summary>The <c>.text</c> of <c>Simd/Native/loongarch64.S</c> up to the last production kernel.
    /// <para><c>Simd/Native/loongarch64.S</c> 到最后一个生产内核为止的 <c>.text</c>。</para></summary>
    private static readonly byte[] LoongArchMachineCode =
    [
        0x14, 0x80, 0xc0, 0x02, 0xd4, 0x24, 0x00, 0x68, 0x80, 0x00, 0x80, 0x2c, 0xa1, 0x00, 0x80, 0x2c,
        0x00, 0x04, 0x27, 0x75, 0x80, 0x00, 0xc0, 0x2c, 0x84, 0x80, 0xc0, 0x02, 0xa5, 0x80, 0xc0, 0x02,
        0xc6, 0x80, 0xff, 0x02, 0xff, 0xdf, 0xff, 0x53, 0x20, 0x00, 0x00, 0x4c, 0xa3, 0x00, 0x9f, 0x76,
        0x14, 0x80, 0xc0, 0x02, 0xd4, 0x1c, 0x00, 0x68, 0x80, 0x00, 0x80, 0x2c, 0x00, 0x0c, 0x27, 0x75,
        0x80, 0x00, 0xc0, 0x2c, 0x84, 0x80, 0xc0, 0x02, 0xc6, 0x80, 0xff, 0x02, 0xff, 0xe7, 0xff, 0x53,
        0x20, 0x00, 0x00, 0x4c, 0x18, 0x00, 0x9f, 0x76, 0x08, 0x08, 0x9f, 0x76, 0x09, 0x08, 0x9f, 0x76,
        0x0a, 0x08, 0x9f, 0x76, 0x0b, 0x08, 0x9f, 0x76, 0x14, 0x80, 0xc0, 0x02, 0xb4, 0x3c, 0x00, 0x68,
        0x80, 0x00, 0x80, 0x2c, 0x02, 0x03, 0x1a, 0x75, 0x03, 0x03, 0x1c, 0x75, 0x04, 0x8b, 0x1a, 0x75,
        0x05, 0x8b, 0x1c, 0x75, 0x06, 0x8f, 0x1a, 0x75, 0x07, 0x8f, 0x1c, 0x75, 0x08, 0x11, 0x0b, 0x74,
        0x29, 0x15, 0x0b, 0x74, 0x4a, 0x19, 0x0b, 0x74, 0x6b, 0x1d, 0x0b, 0x74, 0x84, 0x80, 0xc0, 0x02,
        0xa5, 0x80, 0xff, 0x02, 0xff, 0xc7, 0xff, 0x53, 0x08, 0x29, 0x0b, 0x74, 0x29, 0x2d, 0x0b, 0x74,
        0x0c, 0xc1, 0xef, 0x76, 0x0d, 0xc5, 0xef, 0x76, 0x0e, 0xc9, 0xef, 0x76, 0x0f, 0xcd, 0xef, 0x76,
        0x10, 0xd1, 0xef, 0x76, 0x11, 0xd5, 0xef, 0x76, 0x12, 0xd9, 0xef, 0x76, 0x13, 0xdd, 0xef, 0x76,
        0x84, 0xb5, 0x10, 0x00, 0x84, 0xb8, 0x10, 0x00, 0x84, 0xbc, 0x10, 0x00, 0x84, 0xc0, 0x10, 0x00,
        0x84, 0xc4, 0x10, 0x00, 0x84, 0xc8, 0x10, 0x00, 0x84, 0xcc, 0x10, 0x00, 0x2c, 0xc1, 0xef, 0x76,
        0x2d, 0xc5, 0xef, 0x76, 0x2e, 0xc9, 0xef, 0x76, 0x2f, 0xcd, 0xef, 0x76, 0x30, 0xd1, 0xef, 0x76,
        0x31, 0xd5, 0xef, 0x76, 0x32, 0xd9, 0xef, 0x76, 0x33, 0xdd, 0xef, 0x76, 0x84, 0xb0, 0x10, 0x00,
        0x84, 0xb4, 0x10, 0x00, 0x84, 0xb8, 0x10, 0x00, 0x84, 0xbc, 0x10, 0x00, 0x84, 0xc0, 0x10, 0x00,
        0x84, 0xc4, 0x10, 0x00, 0x84, 0xc8, 0x10, 0x00, 0x84, 0xcc, 0x10, 0x00, 0x20, 0x00, 0x00, 0x4c,
        0x04, 0x04, 0x9f, 0x76, 0x14, 0x40, 0xc0, 0x02, 0xb4, 0x18, 0x00, 0x68, 0x80, 0x00, 0x80, 0x2c,
        0x84, 0x00, 0x27, 0x75, 0x84, 0x80, 0xc0, 0x02, 0xa5, 0xc0, 0xff, 0x02, 0xff, 0xeb, 0xff, 0x53,
        0x8c, 0xc0, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00, 0xa4, 0xb9, 0x15, 0x00,
        0x8c, 0xc4, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00,
        0x84, 0xb8, 0x15, 0x00, 0x8c, 0xc8, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00,
        0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00, 0x8c, 0xcc, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00,
        0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00, 0x8c, 0xd0, 0xef, 0x76,
        0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00,
        0x8c, 0xd4, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00,
        0x84, 0xb8, 0x15, 0x00, 0x8c, 0xd8, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00,
        0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00, 0x8c, 0xdc, 0xef, 0x76, 0x8d, 0x81, 0x6f, 0x00,
        0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00, 0x20, 0x00, 0x00, 0x4c,
        0x14, 0x40, 0xc0, 0x02, 0xd4, 0x24, 0x00, 0x68, 0x80, 0x00, 0x00, 0x2c, 0xa1, 0x00, 0x00, 0x2c,
        0x00, 0x04, 0x27, 0x71, 0x80, 0x00, 0x40, 0x2c, 0x84, 0x40, 0xc0, 0x02, 0xa5, 0x40, 0xc0, 0x02,
        0xc6, 0xc0, 0xff, 0x02, 0xff, 0xdf, 0xff, 0x53, 0x20, 0x00, 0x00, 0x4c, 0xa3, 0x00, 0x9f, 0x72,
        0x14, 0x40, 0xc0, 0x02, 0xd4, 0x1c, 0x00, 0x68, 0x80, 0x00, 0x00, 0x2c, 0x00, 0x0c, 0x27, 0x71,
        0x80, 0x00, 0x40, 0x2c, 0x84, 0x40, 0xc0, 0x02, 0xc6, 0xc0, 0xff, 0x02, 0xff, 0xe7, 0xff, 0x53,
        0x20, 0x00, 0x00, 0x4c, 0x18, 0x00, 0x9f, 0x72, 0x08, 0x08, 0x9f, 0x72, 0x09, 0x08, 0x9f, 0x72,
        0x0a, 0x08, 0x9f, 0x72, 0x0b, 0x08, 0x9f, 0x72, 0x14, 0x40, 0xc0, 0x02, 0xb4, 0x3c, 0x00, 0x68,
        0x80, 0x00, 0x00, 0x2c, 0x02, 0x03, 0x1a, 0x71, 0x03, 0x03, 0x1c, 0x71, 0x04, 0x8b, 0x1a, 0x71,
        0x05, 0x8b, 0x1c, 0x71, 0x06, 0x8f, 0x1a, 0x71, 0x07, 0x8f, 0x1c, 0x71, 0x08, 0x11, 0x0b, 0x70,
        0x29, 0x15, 0x0b, 0x70, 0x4a, 0x19, 0x0b, 0x70, 0x6b, 0x1d, 0x0b, 0x70, 0x84, 0x40, 0xc0, 0x02,
        0xa5, 0xc0, 0xff, 0x02, 0xff, 0xc7, 0xff, 0x53, 0x08, 0x29, 0x0b, 0x70, 0x29, 0x2d, 0x0b, 0x70,
        0x0c, 0xe1, 0xef, 0x72, 0x0d, 0xe5, 0xef, 0x72, 0x0e, 0xe9, 0xef, 0x72, 0x0f, 0xed, 0xef, 0x72,
        0x84, 0xb5, 0x10, 0x00, 0x84, 0xb8, 0x10, 0x00, 0x84, 0xbc, 0x10, 0x00, 0x2c, 0xe1, 0xef, 0x72,
        0x2d, 0xe5, 0xef, 0x72, 0x2e, 0xe9, 0xef, 0x72, 0x2f, 0xed, 0xef, 0x72, 0x84, 0xb0, 0x10, 0x00,
        0x84, 0xb4, 0x10, 0x00, 0x84, 0xb8, 0x10, 0x00, 0x84, 0xbc, 0x10, 0x00, 0x20, 0x00, 0x00, 0x4c,
        0x04, 0x04, 0x9f, 0x72, 0x14, 0x20, 0xc0, 0x02, 0xb4, 0x18, 0x00, 0x68, 0x80, 0x00, 0x00, 0x2c,
        0x84, 0x00, 0x27, 0x71, 0x84, 0x40, 0xc0, 0x02, 0xa5, 0xe0, 0xff, 0x02, 0xff, 0xeb, 0xff, 0x53,
        0x8c, 0xe0, 0xef, 0x72, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00, 0xa4, 0xb9, 0x15, 0x00,
        0x8c, 0xe4, 0xef, 0x72, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00,
        0x84, 0xb8, 0x15, 0x00, 0x8c, 0xe8, 0xef, 0x72, 0x8d, 0x81, 0x6f, 0x00, 0x8e, 0xc1, 0x7f, 0x00,
        0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00, 0x8c, 0xec, 0xef, 0x72, 0x8d, 0x81, 0x6f, 0x00,
        0x8e, 0xc1, 0x7f, 0x00, 0x84, 0xb4, 0x15, 0x00, 0x84, 0xb8, 0x15, 0x00, 0x20, 0x00, 0x00, 0x4c,
    ];

    /// <summary>A <c>vsetvli a0, zero, e8, m8, ta, ma</c> + <c>ret</c> probe whose return value equals the byte
    /// count of one full e8m8 iteration. <para><c>vsetvli a0, zero, e8, m8, ta, ma</c> 加 <c>ret</c> 的探针，
    /// 返回值等于一轮完整 e8m8 迭代的字节数。</para></summary>
    private static readonly byte[] RiscVVectorWidthProbe = [0x57, 0x75, 0x30, 0x0c, 0x82, 0x80];

    private static bool _loongArchAttempted;
    private static bool _riscVAttempted;
    private static bool _loongArchReady;
    private static bool _loongArchWide;
    private static bool _riscVReady;
    private static int _riscVBlockBytes = 16;
    private static string? _activationFailure;

    internal static delegate* unmanaged[Cdecl]<byte*, byte*, nuint, void> LoongArchXor256;
    internal static delegate* unmanaged[Cdecl]<byte*, byte, nuint, void> LoongArchXorConstant256;
    internal static delegate* unmanaged[Cdecl]<byte*, nuint, uint> LoongArchSum256;
    internal static delegate* unmanaged[Cdecl]<ushort*, nuint, ushort> LoongArchXorFold256;
    internal static delegate* unmanaged[Cdecl]<byte*, byte*, nuint, void> LoongArchXor128;
    internal static delegate* unmanaged[Cdecl]<byte*, byte, nuint, void> LoongArchXorConstant128;
    internal static delegate* unmanaged[Cdecl]<byte*, nuint, uint> LoongArchSum128;
    internal static delegate* unmanaged[Cdecl]<ushort*, nuint, ushort> LoongArchXorFold128;
    internal static delegate* unmanaged[Cdecl]<byte*, byte*, nuint, void> RiscVXor;
    internal static delegate* unmanaged[Cdecl]<byte*, byte, nuint, void> RiscVXorConstant;
    internal static delegate* unmanaged[Cdecl]<byte*, nuint, uint> RiscVSum;
    internal static delegate* unmanaged[Cdecl]<ushort*, nuint, ushort> RiscVXorFold;

    /// <summary>Gets whether the LoongArch kernels are emitted and self-tested.
    /// <para>获取 LoongArch 内核是否已发射并通过自测。</para></summary>
    internal static bool LoongArchReady => _loongArchReady;

    /// <summary>Gets whether the active LoongArch kernels use the 256-bit LASX form instead of LSX.
    /// <para>获取激活的 LoongArch 内核是否使用 256 位 LASX 形态而非 LSX。</para></summary>
    internal static bool LoongArchWide => _loongArchWide;

    /// <summary>Gets whether the RISC-V kernels are emitted and self-tested.
    /// <para>获取 RISC-V 内核是否已发射并通过自测。</para></summary>
    internal static bool RiscVReady => _riscVReady;

    /// <summary>Gets the byte count one full RISC-V vector iteration consumes; only meaningful when
    /// <see cref="RiscVReady"/>. <para>获取 RISC-V 向量一轮完整迭代消费的字节数；仅在 <see cref="RiscVReady"/>
    /// 时有意义。</para></summary>
    internal static int RiscVBlockBytes => _riscVBlockBytes;

    /// <summary>Gets why the native kernels are not active, or <see langword="null"/> when they are active
    /// or the architecture does not use them. <para>获取原生内核未激活的原因；内核已激活或架构不使用
    /// 它们时为 <see langword="null"/>。</para></summary>
    internal static string? ActivationFailure => _activationFailure;

    /// <summary>
    /// Emits and self-tests the LoongArch kernels once, preferring LASX when the processor advertises it.
    /// <para>一次性发射并自测 LoongArch 内核，处理器宣告 LASX 时优先使用。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the kernels are ready for dispatch.
    /// <para>内核可被分派使用时为 <see langword="true"/>。</para></returns>
    internal static bool TryActivateLoongArch()
    {
        if (_loongArchAttempted)
        {
            return _loongArchReady;
        }

        _loongArchAttempted = true;
        if (!NativeCpuFeatures.TryGetLoongArchFeatures(out var lsx, out var lasx))
        {
            _activationFailure = "loongarch-hwcap-unavailable";
            return false;
        }

        if (!lsx && !lasx)
        {
            _activationFailure = "loongarch-vector-unsupported";
            return false;
        }

        _loongArchWide = lasx;
        if (!ExecutableCodePage.TryAllocate(LoongArchMachineCode, out var page))
        {
            _activationFailure = "loongarch-emission-failed:" + ExecutableCodePage.LastFailure;
            return false;
        }

        var basePointer = (byte*)page;
        if (_loongArchWide)
        {
            LoongArchXor256 = (delegate* unmanaged[Cdecl]<byte*, byte*, nuint, void>)(void*)(basePointer + LoongArchXor256Offset);
            LoongArchXorConstant256 = (delegate* unmanaged[Cdecl]<byte*, byte, nuint, void>)(void*)(basePointer + LoongArchXorConstant256Offset);
            LoongArchSum256 = (delegate* unmanaged[Cdecl]<byte*, nuint, uint>)(void*)(basePointer + LoongArchSum256Offset);
            LoongArchXorFold256 = (delegate* unmanaged[Cdecl]<ushort*, nuint, ushort>)(void*)(basePointer + LoongArchXorFold256Offset);
        }
        else
        {
            LoongArchXor128 = (delegate* unmanaged[Cdecl]<byte*, byte*, nuint, void>)(void*)(basePointer + LoongArchXor128Offset);
            LoongArchXorConstant128 = (delegate* unmanaged[Cdecl]<byte*, byte, nuint, void>)(void*)(basePointer + LoongArchXorConstant128Offset);
            LoongArchSum128 = (delegate* unmanaged[Cdecl]<byte*, nuint, uint>)(void*)(basePointer + LoongArchSum128Offset);
            LoongArchXorFold128 = (delegate* unmanaged[Cdecl]<ushort*, nuint, ushort>)(void*)(basePointer + LoongArchXorFold128Offset);
        }

        var selfTestFailure = RunLoongArchSelfTest(_loongArchWide);
        if (selfTestFailure is not null)
        {
            LoongArchXor256 = null;
            LoongArchXorConstant256 = null;
            LoongArchSum256 = null;
            LoongArchXorFold256 = null;
            LoongArchXor128 = null;
            LoongArchXorConstant128 = null;
            LoongArchSum128 = null;
            LoongArchXorFold128 = null;
            _activationFailure = "loongarch-selftest-failed:" + selfTestFailure;
            return false;
        }

        _activationFailure = null;
        _loongArchReady = true;
        return true;
    }

    /// <summary>
    /// Emits and self-tests the RISC-V kernels once, measuring the vector width through an emitted probe.
    /// <para>一次性发射并自测 RISC-V 内核，向量宽度经发射的探针实测。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the kernels are ready for dispatch.
    /// <para>内核可被分派使用时为 <see langword="true"/>。</para></returns>
    internal static bool TryActivateRiscV()
    {
        if (_riscVAttempted)
        {
            return _riscVReady;
        }

        _riscVAttempted = true;
        if (!NativeCpuFeatures.TryGetRiscVVectorFlag(out var vector) || !vector)
        {
            _activationFailure = "riscv-hwcap-unavailable";
            return false;
        }

        if (!ExecutableCodePage.TryAllocate(RiscVVectorWidthProbe, out var probePage))
        {
            _activationFailure = "riscv-probe-emission-failed:" + ExecutableCodePage.LastFailure;
            return false;
        }

        var probe = (delegate* unmanaged[Cdecl]<nuint>)(void*)probePage;
        var blockBytes = probe();
        if (blockBytes < 16)
        {
            _activationFailure = "riscv-vector-width-invalid:" + blockBytes;
            return false;
        }

        if (!ExecutableCodePage.TryAllocate(RiscVMachineCode, out var page))
        {
            _activationFailure = "riscv-emission-failed:" + ExecutableCodePage.LastFailure;
            return false;
        }

        var basePointer = (byte*)page;
        RiscVXor = (delegate* unmanaged[Cdecl]<byte*, byte*, nuint, void>)(void*)(basePointer + RiscVXorOffset);
        RiscVXorConstant = (delegate* unmanaged[Cdecl]<byte*, byte, nuint, void>)(void*)(basePointer + RiscVXorConstantOffset);
        RiscVSum = (delegate* unmanaged[Cdecl]<byte*, nuint, uint>)(void*)(basePointer + RiscVSumOffset);
        RiscVXorFold = (delegate* unmanaged[Cdecl]<ushort*, nuint, ushort>)(void*)(basePointer + RiscVXorFoldOffset);
        _riscVBlockBytes = (int)blockBytes;

        var selfTestFailure = RunRiscVSelfTest();
        if (selfTestFailure is not null)
        {
            RiscVXor = null;
            RiscVXorConstant = null;
            RiscVSum = null;
            RiscVXorFold = null;
            _activationFailure = "riscv-selftest-failed:" + selfTestFailure;
            return false;
        }

        _activationFailure = null;
        _riscVReady = true;
        return true;
    }

    /// <summary>
    /// Compares the LoongArch kernels of the active width against a local scalar oracle on deterministic patterns.
    /// <para>在确定性数据上把激活宽度的 LoongArch 内核与本地标量预言比对。</para>
    /// </summary>
    /// <param name="wide">Whether the 256-bit LASX pointers were wired.
    /// <para>是否接好 256 位 LASX 指针。</para></param>
    /// <returns><see langword="null"/> when every kernel matched its oracle, otherwise a short failure code.
    /// <para>每个内核都与预言一致时为 <see langword="null"/>，否则为简短失败码。</para></returns>
    private static string? RunLoongArchSelfTest(bool wide)
    {
        Span<byte> left = stackalloc byte[320 + 32];
        Span<byte> right = stackalloc byte[320 + 32];
        Span<byte> actual = stackalloc byte[320 + 32];
        Span<byte> expectedXor = stackalloc byte[320];
        Span<byte> expectedConstant = stackalloc byte[320];
        Fill(left, 37);
        Fill(right, 91);
        var width = wide ? 32 : 16;
        fixed (byte* leftBase = left)
        fixed (byte* rightBase = right)
        fixed (byte* actualBase = actual)
        {
            var alignOffset = (int)(((nuint)0 - (nuint)leftBase) & (nuint)(wide ? 31u : 15u));
            var leftAligned = left[alignOffset..];
            var rightAligned = right[alignOffset..];
            var actualAligned = actual[alignOffset..];
            fixed (byte* leftPointer = leftAligned)
            fixed (byte* rightPointer = rightAligned)
            fixed (byte* actualPointer = actualAligned)
            {
                foreach (var length in new[] { width, width * 4, 320 })
                {
                    for (var index = 0; index < length; index++)
                    {
                        expectedXor[index] = (byte)(leftAligned[index] ^ rightAligned[index]);
                        expectedConstant[index] = (byte)(expectedXor[index] ^ 0xA5);
                    }

                    leftAligned[..length].CopyTo(actualAligned);
                    if (wide)
                    {
                        LoongArchXor256(actualPointer, rightPointer, (nuint)length);
                    }
                    else
                    {
                        LoongArchXor128(actualPointer, rightPointer, (nuint)length);
                    }

                    if (!Matches(actualAligned, expectedXor, length))
                    {
                        return "xor-" + length;
                    }

                    if (wide)
                    {
                        LoongArchXorConstant256(actualPointer, 0xA5, (nuint)length);
                    }
                    else
                    {
                        LoongArchXorConstant128(actualPointer, 0xA5, (nuint)length);
                    }

                    if (!Matches(actualAligned, expectedConstant, length))
                    {
                        return "xor-const-" + length;
                    }
                }
            }
        }

        // The native sum kernels require naturally aligned pointers; mirror
        // the production wrappers by peeling to the active block width first.
        Span<byte> sumData = stackalloc byte[1024 + 32];
        Fill(sumData, 13);
        ulong expectedSum = 0;
        fixed (byte* sumBase = sumData)
        {
            var offset = (int)(((nuint)0 - (nuint)sumBase) & (nuint)(wide ? 31u : 15u));
            var aligned = sumData[offset..];
            aligned = aligned[..(aligned.Length / (wide ? 32 : 16) * (wide ? 32 : 16))];
            foreach (var value in aligned)
            {
                expectedSum += value;
            }

            fixed (byte* sumPointer = aligned)
            {
                var actualSum = wide
                    ? LoongArchSum256(sumPointer, (nuint)aligned.Length)
                    : LoongArchSum128(sumPointer, (nuint)aligned.Length);
                if (actualSum != (uint)expectedSum)
                {
                    return "sum:" + actualSum.ToString("x8") + ":" + ((uint)expectedSum).ToString("x8") + ":" + aligned.Length + ":" + (wide ? "w" : "n");
                }
            }
        }

        Span<ushort> words = stackalloc ushort[640 + 16];
        ushort expectedFold = 0;
        fixed (ushort* wordBase = words)
        {
            var wordOffset = (int)(((nuint)0 - (nuint)wordBase) & (nuint)(wide ? 31u : 15u));
            if ((wordOffset & 1) != 0)
            {
                wordOffset += 1;
            }

            var alignedWords = words[wordOffset..];
            alignedWords = alignedWords[..(alignedWords.Length / (wide ? 16 : 8) * (wide ? 16 : 8))];
            for (var index = 0; index < alignedWords.Length; index++)
            {
                alignedWords[index] = (ushort)(index * 37 + 11);
                expectedFold ^= alignedWords[index];
            }

            fixed (ushort* wordPointer = alignedWords)
            {
                var actualFold = wide
                    ? LoongArchXorFold256(wordPointer, (nuint)alignedWords.Length)
                    : LoongArchXorFold128(wordPointer, (nuint)alignedWords.Length);
                return actualFold == expectedFold ? null : "xor-fold";
            }
        }
    }

    /// <summary>
    /// Compares the RISC-V kernels against a local scalar oracle on deterministic patterns.
    /// <para>在确定性数据上把 RISC-V 内核与本地标量预言比对。</para>
    /// </summary>
    /// <returns><see langword="null"/> when every kernel matched its oracle, otherwise a short failure code.
    /// <para>每个内核都与预言一致时为 <see langword="null"/>，否则为简短失败码。</para></returns>
    private static string? RunRiscVSelfTest()
    {
        Span<byte> left = stackalloc byte[1024];
        Span<byte> right = stackalloc byte[1024];
        Span<byte> actual = stackalloc byte[1024];
        Span<byte> expected = stackalloc byte[1024];
        Fill(left, 37);
        Fill(right, 91);
        for (var index = 0; index < left.Length; index++)
        {
            expected[index] = (byte)(left[index] ^ right[index]);
        }

        left.CopyTo(actual);
        fixed (byte* actualPointer = actual)
        fixed (byte* rightPointer = right)
        {
            RiscVXor(actualPointer, rightPointer, (nuint)actual.Length);
            if (!Matches(actual, expected, actual.Length))
            {
                return "xor";
            }

            for (var index = 0; index < expected.Length; index++)
            {
                expected[index] = (byte)(expected[index] ^ 0xA5);
            }

            RiscVXorConstant(actualPointer, 0xA5, (nuint)actual.Length);
        }

        if (!Matches(actual, expected, actual.Length))
        {
            return "xor-const";
        }

        ulong expectedSum = 0;
        foreach (var value in right)
        {
            expectedSum += value;
        }

        fixed (byte* sumPointer = right)
        {
            var sum = RiscVSum(sumPointer, (nuint)right.Length);
            if (sum != (uint)expectedSum)
            {
                return "sum";
            }
        }

        Span<ushort> words = stackalloc ushort[1024];
        ushort expectedFold = 0;
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = (ushort)(index * 37 + 11);
            expectedFold ^= words[index];
        }

        fixed (ushort* wordPointer = words)
        {
            return RiscVXorFold(wordPointer, (nuint)words.Length) == expectedFold ? null : "xor-fold";
        }
    }

    private static void Fill(Span<byte> data, byte seed)
    {
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(seed + index * 31);
        }
    }

    private static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, int length)
    {
        for (var index = 0; index < length; index++)
        {
            if (actual[index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }
}