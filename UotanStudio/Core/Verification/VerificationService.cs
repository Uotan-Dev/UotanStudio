using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Services;

namespace UotanStudio.Core.Verification;

/// <summary>
/// <para>完整性校验条目：记录单个部件的路径、大小与 SHA-256。</para>
/// Integrity entry: records the path, size, and SHA-256 of a single component.
/// </summary>
/// <param name="Path">
/// <para>部件文件路径。</para>
/// The component file path.
/// </param>
/// <param name="Size">
/// <para>部件文件大小（字节）。</para>
/// The component file size in bytes.
/// </param>
/// <param name="Sha256">
/// <para>部件文件 SHA-256（小写十六进制）。</para>
/// The component file SHA-256 (lower-case hex).
/// </param>
public sealed record IntegrityEntry(string Path, long Size, string Sha256);

/// <summary>
/// <para>完整性校验结果，包含全部部件的哈希清单。</para>
/// Integrity check result, containing the hash manifest of all components.
/// </summary>
public sealed class IntegrityCheckResult
{
    /// <summary>
    /// <para>获取或设置部件的完整性清单。</para>
    /// Gets or sets the integrity manifest of the components.
    /// </summary>
    public IReadOnlyList<IntegrityEntry> Entries { get; set; } = [];

    /// <summary>
    /// <para>获取或设置是否全部校验通过（所有文件均可读并完成哈希计算）。</para>
    /// Gets or sets whether the whole check succeeded (all files readable and hashed).
    /// </summary>
    public bool Success { get; set; }
}

/// <summary>
/// <para>验证服务：AVB 签名校验与部件清单完整性校验。</para>
/// Verification service: AVB signature verification and component integrity verification.
/// </summary>
public sealed class VerificationService
{
    private static readonly Lazy<VerificationService> LazyInstance = new(() => new VerificationService());

    /// <summary>
    /// <para>获取 <see cref="VerificationService"/> 单例实例。</para>
    /// Gets the <see cref="VerificationService"/> singleton instance.
    /// </summary>
    public static VerificationService Instance => LazyInstance.Value;

    private VerificationService() { }

    /// <summary>
    /// <para>校验 AVB 镜像签名（包装 <see cref="FirmwareService.VerifyAvbAsync"/>）。</para>
    /// Verifies an AVB image signature (wraps <see cref="FirmwareService.VerifyAvbAsync"/>).
    /// </summary>
    /// <param name="vbmetaPath">
    /// <para>vbmeta 镜像路径。</para>
    /// The vbmeta image path.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    /// <returns>
    /// <para>固件操作结果。</para>
    /// The firmware operation result.
    /// </returns>
    public Task<FirmwareOpResult> VerifyAvbAsync(string vbmetaPath, CancellationToken cancellationToken = default)
        => FirmwareService.Instance.VerifyAvbAsync(vbmetaPath, cancellationToken);

    /// <summary>
    /// <para>对部件文件列表逐一计算 SHA-256 并返回完整性清单。</para>
    /// Computes SHA-256 for each component file in the list and returns the integrity manifest.
    /// </summary>
    /// <param name="componentPaths">
    /// <para>部件文件路径列表。</para>
    /// The list of component file paths.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    /// <returns>
    /// <para>完整性校验结果。</para>
    /// The integrity check result.
    /// </returns>
    public async Task<IntegrityCheckResult> VerifyIntegrityAsync(IEnumerable<string> componentPaths, CancellationToken cancellationToken = default)
    {
        var entries = new List<IntegrityEntry>();
        var success = true;
        foreach (var path in componentPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                success = false;
                continue;
            }

            try
            {
                var hash = await HashService.Sha256Async(path, cancellationToken).ConfigureAwait(false);
                var size = new FileInfo(path).Length;
                entries.Add(new IntegrityEntry(Path.GetFullPath(path), size, hash));
            }
            catch (Exception)
            {
                success = false;
            }
        }

        return new IntegrityCheckResult { Entries = entries, Success = success };
    }
}