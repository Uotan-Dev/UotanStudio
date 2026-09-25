using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Kernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// KernelPatch/APatch 镜像注入回归测试：把真机 arm64 Image 头（MZ + 有效分支 + ARM\x64 魔数）与合成 kpimg
/// 组合，验证补丁成功、入口被重定向为分支、预设字段（kernel_size/page_shift/image_offset）与头部一致，以及非
/// arm64 内核被拒绝。用于保证把头部解析抽到 <see cref="Arm64ImageHeader"/> 后行为不变。
/// <para>Regression tests for the KernelPatch/APatch image injector: they combine a real arm64 Image header
/// (MZ + valid branch + ARM\x64 magic) with a synthetic kpimg, verifying the patch succeeds, the entry is
/// redirected to a branch, the preset fields (kernel_size/page_shift/image_offset) match the header, and a
/// non-arm64 kernel is rejected — guarding that moving header parsing into <see cref="Arm64ImageHeader"/> is
/// behavior-preserving.</para>
/// </summary>
public sealed class KernelPatchInjectorTests
{
    private const int SetupKernelSize = 0x40 + 0x18;
    private const int SetupPageShift = 0x40 + 0x20;
    private const int SetupImageOffset = 0x40 + 0x28;

    [Fact]
    public void Patch_ValidArm64Kernel_AppendsKpimgAndRedirectsEntry()
    {
        var kernel = BuildKernel(imageSize: 0x2d60000);
        var kpimg = BuildKpimg();
        var output = KernelPatchInjector.Patch(kernel, kpimg, "password1");

        Assert.Equal(0x1000 + 0x1000 + 0x80, output.Length);
        var entry = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4));
        Assert.Equal(0x14000000u, entry & 0xfc000000u);

        var preset = output.AsSpan(0x1000);
        Assert.Equal(0x2d60000L, BinaryPrimitives.ReadInt64LittleEndian(preset[SetupKernelSize..]));
        Assert.Equal(12, BinaryPrimitives.ReadInt64LittleEndian(preset[SetupPageShift..]));
        Assert.Equal(0x1000, BinaryPrimitives.ReadInt64LittleEndian(preset[SetupImageOffset..]));
    }

    [Fact]
    public void Patch_RejectsNonArm64Kernel()
    {
        var kernel = BuildKernel(imageSize: 0x4000);
        kernel[0x38] = 0x00;
        Assert.Throws<NotSupportedException>(() => KernelPatchInjector.Patch(kernel, BuildKpimg(), "password1"));
    }

    private static byte[] BuildKernel(ulong imageSize)
    {
        var kernel = new byte[0x400];
        kernel[0] = 0x4d; kernel[1] = 0x5a;
        BinaryPrimitives.WriteUInt32LittleEndian(kernel.AsSpan(4), 0x1449ffff);
        BinaryPrimitives.WriteUInt64LittleEndian(kernel.AsSpan(0x10), imageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(kernel.AsSpan(0x18), 0x0a);
        "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
        "CONFIG_KALLSYMS=y"u8.CopyTo(kernel.AsSpan(0x100));
        return kernel;
    }

    private static byte[] BuildKpimg()
    {
        var kpimg = new byte[0x1000];
        "KP1158"u8.CopyTo(kpimg);
        BinaryPrimitives.WriteUInt64LittleEndian(kpimg.AsSpan(0x10), 0);
        return kpimg;
    }
}