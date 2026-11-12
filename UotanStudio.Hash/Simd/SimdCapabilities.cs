using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ArmAes = System.Runtime.Intrinsics.Arm.Aes;
using ArmCrc = System.Runtime.Intrinsics.Arm.Crc32;
using ArmDp = System.Runtime.Intrinsics.Arm.Dp;
using ArmSha1 = System.Runtime.Intrinsics.Arm.Sha1;
using ArmSha256 = System.Runtime.Intrinsics.Arm.Sha256;

#pragma warning disable SYSLIB5003
namespace UotanStudio.Hash.Simd;

/// <summary>
/// Reports the vector instruction families available on the running processor and the kernel that the byte-block
/// primitives selected.
/// <para>报告当前处理器可用的向量指令族，以及字节块原语所选用的内核。</para>
/// </summary>
/// <remarks>
/// ARM SVE intrinsics remain marked experimental by the base class library, so this file suppresses SYSLIB5003
/// locally; no project-wide suppression is applied. Every reported family has a scalar software fallback and detection
/// is performed once during type initialization.
/// <para>基础类库仍将 ARM SVE intrinsic 标记为实验性，因此本文件在本地抑制 SYSLIB5003，不做项目级抑制。
/// 所有报告出的指令族都有标量软件回退，检测只在类型初始化时执行一次。</para>
/// </remarks>
public static class SimdCapabilities
{
    private static readonly SimdIsa SupportedValue = DetectSupported();

    /// <summary>
    /// Gets every vector instruction family detected on the current processor, including <see cref="SimdIsa.Scalar"/>.
    /// <para>获取本机检测到的全部向量指令族，其中包含 <see cref="SimdIsa.Scalar"/>。</para>
    /// </summary>
    public static SimdIsa SupportedIsas => SupportedValue;

    /// <summary>
    /// Gets the instruction family behind the kernel currently selected for byte-block operations.
    /// <para>获取字节块运算当前所选内核对应的指令族。</para>
    /// </summary>
    public static SimdIsa ActiveIsa => ToIsa(SimdOps.ActiveKernel);

    /// <summary>
    /// Gets the number of bytes one full iteration of the active kernel consumes; <c>1</c> for the scalar fallback.
    /// <para>获取所选内核每轮完整迭代消费的字节数；标量回退时为 <c>1</c>。</para>
    /// </summary>
    public static int ActiveVectorWidthBytes => WidthOf(SimdOps.ActiveKernel);

    /// <summary>
    /// Gets whether byte-block operations execute on a vector kernel instead of the scalar software fallback.
    /// <para>获取字节块运算是否走向量内核，而非标量软件回退。</para>
    /// </summary>
    public static bool HardwareAccelerated => SimdOps.ActiveKernel != SimdOps.SimdKernel.Software;

    /// <summary>
    /// Returns a compact diagnostic line naming the architecture, every supported family, the active family and its
    /// block width.
    /// <para>返回一行紧凑的诊断信息，列出架构、全部可用指令族、当前指令族及块宽度。</para>
    /// </summary>
    /// <returns>A semicolon-separated report such as <c>arch=X64;supported=Scalar|...;active=Avx512;width=64</c>.
    /// <para>以分号分隔的报告，例如 <c>arch=X64;supported=Scalar|...;active=Avx512;width=64</c>。</para></returns>
    public static string Describe()
        => $"arch={RuntimeInformation.OSArchitecture};supported={Names(SupportedValue)};active={ActiveIsa};width={ActiveVectorWidthBytes}";

    /// <summary>
    /// Maps a block kernel to the instruction family that executes it.
    /// <para>把块内核映射到执行它的指令族。</para>
    /// </summary>
    /// <param name="kernel">Kernel to describe. <para>待描述的内核。</para></param>
    /// <returns>The matching family. <para>对应的指令族。</para></returns>
    internal static SimdIsa ToIsa(SimdOps.SimdKernel kernel) => kernel switch
    {
        SimdOps.SimdKernel.Sve => SimdIsa.Sve,
        SimdOps.SimdKernel.Vector512 => Vector512.IsHardwareAccelerated && (Avx10v1.V512.IsSupported || Avx10v2.V512.IsSupported)
            ? SimdIsa.Avx10
            : SimdIsa.Avx512,
        SimdOps.SimdKernel.Vector256 => SimdIsa.Avx2,
        SimdOps.SimdKernel.Vector128 => Sse2.IsSupported ? SimdIsa.Sse2 : SimdIsa.AdvSimd,
        SimdOps.SimdKernel.PortableVectors => SimdIsa.PortableVectors,
        _ => SimdIsa.Scalar,
    };

    /// <summary>
    /// Returns the block width in bytes for a kernel.
    /// <para>返回内核的块宽度（字节）。</para>
    /// </summary>
    /// <param name="kernel">Kernel to measure. <para>待测量的内核。</para></param>
    /// <returns>The number of bytes consumed per full iteration. <para>每轮完整迭代消费的字节数。</para></returns>
    internal static int WidthOf(SimdOps.SimdKernel kernel) => kernel switch
    {
        SimdOps.SimdKernel.Sve or SimdOps.SimdKernel.PortableVectors => System.Numerics.Vector<byte>.Count,
        SimdOps.SimdKernel.Vector512 => Vector512<byte>.Count,
        SimdOps.SimdKernel.Vector256 => Vector256<byte>.Count,
        SimdOps.SimdKernel.Vector128 => Vector128<byte>.Count,
        _ => 1,
    };

    private static SimdIsa DetectSupported()
    {
        var isas = SimdIsa.Scalar;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            isas |= SimdIsa.PortableVectors;
        }

        if (Sse2.IsSupported)
        {
            isas |= SimdIsa.Sse2;
        }

        if (Avx2.IsSupported)
        {
            isas |= SimdIsa.Avx2;
        }

        if (Avx512F.IsSupported || Avx512BW.IsSupported)
        {
            isas |= SimdIsa.Avx512;
        }

        if (Avx10v1.IsSupported || Avx10v2.IsSupported || Avx10v1.V512.IsSupported || Avx10v2.V512.IsSupported)
        {
            isas |= SimdIsa.Avx10;
        }

        if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported)
        {
            isas |= SimdIsa.AdvSimd;
        }

        if (ArmCrc.IsSupported)
        {
            isas |= SimdIsa.ArmCrc;
        }

        if (ArmAes.IsSupported || ArmSha1.IsSupported || ArmSha256.IsSupported || ArmDp.IsSupported)
        {
            isas |= SimdIsa.ArmCrypto;
        }

        if (System.Runtime.Intrinsics.Arm.Sve.IsSupported)
        {
            isas |= SimdIsa.Sve;
        }

        if (System.Runtime.Intrinsics.Arm.Sve2.IsSupported)
        {
            isas |= SimdIsa.Sve2;
        }

        return isas;
    }

    private static string Names(SimdIsa isas)
    {
        var names = new System.Text.StringBuilder();
        AppendName(names, isas, SimdIsa.Scalar);
        AppendName(names, isas, SimdIsa.PortableVectors);
        AppendName(names, isas, SimdIsa.Sse2);
        AppendName(names, isas, SimdIsa.Avx2);
        AppendName(names, isas, SimdIsa.Avx512);
        AppendName(names, isas, SimdIsa.Avx10);
        AppendName(names, isas, SimdIsa.AdvSimd);
        AppendName(names, isas, SimdIsa.ArmCrc);
        AppendName(names, isas, SimdIsa.ArmCrypto);
        AppendName(names, isas, SimdIsa.Sve);
        AppendName(names, isas, SimdIsa.Sve2);
        return names.ToString();
    }

    private static void AppendName(System.Text.StringBuilder builder, SimdIsa isas, SimdIsa flag)
    {
        if ((isas & flag) == 0)
        {
            return;
        }

        if (builder.Length != 0)
        {
            builder.Append('|');
        }

        builder.Append(flag);
    }
}
#pragma warning restore SYSLIB5003