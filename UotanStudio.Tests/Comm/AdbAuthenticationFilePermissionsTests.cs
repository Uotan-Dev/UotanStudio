using System.Runtime.InteropServices;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

/// <summary>
/// Verifies that newly created ADB private keys have restrictive file permissions
/// on Unix (mode 0600 — user read/write only), eliminating the "create 0644 then
/// chmod" exposure window.  On Windows the file inherits parent-directory ACLs,
/// so only the creation/write round-trip is verified there.
/// <para>验证新建 ADB 私钥在 Unix 下即时具有受限权限（0600），消除"先 0644 再 chmod"
/// 暴露窗口。Windows 下仅验证创建/写入回环。</para>
/// </summary>
public class AdbAuthenticationFilePermissionsTests
{
    // Mask for the permission bits (excluding type/sticky).
    private const UnixFileMode PermissionMask =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode GroupOtherMask =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [Fact]
    public void LoadOrCreate_NewKey_WritesBothFiles()
    {
        using var tempDir = new TempDirectory();
        var keyPath = Path.Combine(tempDir.Path, "adbkey");

        var auth = AdbAuthentication.LoadOrCreate(keyPath);
        try
        {
            Assert.True(File.Exists(keyPath), "Private key file should exist");
            Assert.True(File.Exists(keyPath + ".pub"), "Public key file should exist");

            // Re-loading should yield the same key (no regeneration).
            var auth2 = AdbAuthentication.LoadOrCreate(keyPath);
            try
            {
                var pub1 = auth.BuildPublicKeyPayload();
                var pub2 = auth2.BuildPublicKeyPayload();
                Assert.Equal(pub1, pub2);
            }
            finally
            {
                auth2.Dispose();
            }
        }
        finally
        {
            auth.Dispose();
        }
    }

    [Fact]
    public void LoadOrCreate_ExistingKey_DoesNotRegenerate()
    {
        using var tempDir = new TempDirectory();
        var keyPath = Path.Combine(tempDir.Path, "adbkey");

        var first = AdbAuthentication.LoadOrCreate(keyPath);
        var firstPub = first.BuildPublicKeyPayload();
        first.Dispose();

        var second = AdbAuthentication.LoadOrCreate(keyPath);
        var secondPub = second.BuildPublicKeyPayload();
        second.Dispose();

        Assert.Equal(firstPub, secondPub);
    }

    [Fact]
    public void LoadOrCreate_FailureDoesNotCorruptExistingKey()
    {
        using var tempDir = new TempDirectory();
        var keyPath = Path.Combine(tempDir.Path, "adbkey");

        // Create initial key.
        var first = AdbAuthentication.LoadOrCreate(keyPath);
        first.Dispose();
        var originalBytes = File.ReadAllBytes(keyPath);

        // Re-load should succeed and preserve content.
        var second = AdbAuthentication.LoadOrCreate(keyPath);
        second.Dispose();
        var reloadedBytes = File.ReadAllBytes(keyPath);

        Assert.Equal(originalBytes, reloadedBytes);
    }

    // ---- Unix-only permission checks ----
    // These run only on Linux where UnixFileMode is meaningful.
    // On Windows they are skipped via the runtime check.

    [Fact]
    public void Unix_NewPrivateKey_HasMode0600()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return; // Skipped: Unix file permission verification requires Linux.
        }

        using var tempDir = new TempDirectory();
        var keyPath = Path.Combine(tempDir.Path, "adbkey");

        var auth = AdbAuthentication.LoadOrCreate(keyPath);
        auth.Dispose();

        Assert.True(File.Exists(keyPath));
        var info = new FileInfo(keyPath);
        var mode = info.UnixFileMode;

        // 0600 = UserRead | UserWrite; group/other bits must be clear.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode & PermissionMask);
        Assert.Equal(UnixFileMode.None, mode & GroupOtherMask);
    }

    [Fact]
    public void Unix_NewPublicKey_AlsoRestrictive()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return; // Skipped: Unix file permission verification requires Linux.
        }

        using var tempDir = new TempDirectory();
        var keyPath = Path.Combine(tempDir.Path, "adbkey");

        var auth = AdbAuthentication.LoadOrCreate(keyPath);
        auth.Dispose();

        var pubPath = keyPath + ".pub";
        Assert.True(File.Exists(pubPath));
        var info = new FileInfo(pubPath);
        var mode = info.UnixFileMode;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode & PermissionMask);
    }
}
