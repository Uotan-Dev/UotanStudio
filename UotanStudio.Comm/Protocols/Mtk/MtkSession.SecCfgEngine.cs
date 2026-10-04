using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Protocols.Mtk.HwCrypto;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Optional register seam a caller allows for the SEJ-protected seccfg variants.
    /// <para>调用方为 SEJ 保护的 seccfg 变体允许使用的寄存器接缝。</para></summary>
    private sealed class SejRegisterAccess(
        Func<uint, CancellationToken, Task<uint>> read,
        Func<uint, uint, CancellationToken, Task> write) : IMtkSejRegisterAccess
    {
        public Task<uint> ReadWordAsync(uint address, CancellationToken cancellationToken) => read(address, cancellationToken);

        public Task WriteWordAsync(uint address, uint value, CancellationToken cancellationToken) =>
            write(address, value, cancellationToken);
    }

    /// <summary>
    /// Builds the SEJ hash engine for this session's chip, or returns null when the reference chip table
    /// records no SEJ base for it.
    /// <para>为本会话芯片构建 SEJ 摘要引擎；参考芯片表未记录该芯片的 SEJ 基址时返回 null。</para></summary>
    private static MtkSejCryptoEngine? CreateSejEngine(MtkDeviceInfo? device, IMtkSejRegisterAccess access)
    {
        if (device is null || MtkBromChipTable.Find(device.HardwareCode) is not { } profile
            || profile.SejBaseAddress is not { } sejBase)
        {
            return null;
        }

        return new MtkSejCryptoEngine(device.HardwareCode, sejBase, access);
    }

    /// <summary>
    /// Builds the replacement version 4 image for the apply paths. The software transform stays
    /// off-device; every hardware variant is resolved through the running target exactly the way the
    /// reference client does, and stays refused when no register seam or SEJ base is available.
    /// <para>为 Apply 路径构造替换的版本 4 镜像。软件变换保持在设备外；所有硬件变体都按参考客户端的方式
    /// 经运行中的目标解析，在没有寄存器接缝或 SEJ 基址时仍被拒绝。</para></summary>
    private async Task<byte[]> CreateSecCfgReplacementAsync(MtkSecCfgView view, byte[] image,
        MtkSecCfgLockIntent intent, IMtkSejRegisterAccess? registerAccess, CancellationToken cancellationToken)
    {
        if (view.Version4 is null)
        {
            throw new NotSupportedException(
                "The version 3 layout cannot be rewritten off-device because its integrity transform lives in the target SEJ engine.");
        }

        if (view.Version4.HashKind == MtkSecCfgHashKind.Software)
        {
            return MtkSecCfg.CreateVersion4(view.Version4, intent);
        }

        if (registerAccess is null || CreateSejEngine(DeviceInfo, registerAccess) is not { } engine)
        {
            throw new NotSupportedException(
                "The stored hash is not protected by the software transform, so rewriting it requires the target SEJ engine.");
        }

        var resolved = await MtkSecCfg.TryReadVersion4Async(image, engine, cancellationToken).ConfigureAwait(false);
        if (resolved is null || resolved.HashKind == MtkSecCfgHashKind.Unresolved)
        {
            throw new NotSupportedException(
                "The target SEJ engine did not reproduce the stored hash, so the layout cannot be rewritten.");
        }

        return await MtkSecCfg.CreateVersion4Async(resolved, intent, engine, cancellationToken).ConfigureAwait(false);
    }
}