using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Starts an EDL session from the programmer the page holds, whether that is one Sahara image or a multi-image
/// manifest that spreads the programmer across several Sahara image identifiers.
/// <para>用页面持有的 programmer 启动 EDL 会话，无论它是单个 Sahara 镜像，还是把 programmer 分散到多个 Sahara
/// 镜像标识上的多镜像清单。</para>
/// </summary>
/// <remarks>
/// Routing every host call site through this one entry lets the same path accept a single image and a manifest,
/// and the single-image form reaches the session unchanged. A path that does not exist starts the session without
/// a programmer upload instead of failing, because the session may already be configured on that endpoint.
/// <para>所有宿主调用点统一经这一个入口，使同一路径既接受单镜像也接受清单，且单镜像形态原样抵达会话。不存在的
/// 路径改为不带 programmer 启动而不是失败，因为该端点上的会话可能已经配置完成。</para>
/// </remarks>
internal static class EdlProgrammerSession
{
    /// <summary>
    /// Ensures the session is initialized with the programmer at the supplied path.
    /// <para>用所给路径上的 programmer 确保会话已初始化。</para>
    /// </summary>
    /// <param name="session">The session to start; it is opened by the caller's device lease.
    /// <para>要启动的会话；由调用方的设备租约打开。</para></param>
    /// <param name="programmerPath">Path of one programmer image or a multi-image manifest, or
    /// <see langword="null"/> to start without a programmer upload.
    /// <para>单个 programmer 镜像或多镜像清单的路径；为 <see langword="null"/> 时不带 programmer 上传启动。</para></param>
    /// <param name="initialization">Optional session startup requirements.
    /// <para>可选的会话启动要求。</para></param>
    /// <param name="cancellationToken">Cancels the open, the hash-free parse and the handshake.
    /// <para>取消打开、解析与握手。</para></param>
    /// <returns>A task that completes when the session is configured or was already configured.
    /// <para>会话已配置或本就已配置时完成的任务。</para></returns>
    /// <exception cref="InvalidDataException">The manifest cannot be parsed or references a missing image.
    /// <para>清单无法解析或引用了缺失的镜像。</para></exception>
    /// <exception cref="IOException">The programmer file exists but cannot be opened.
    /// <para>programmer 文件存在但无法打开。</para></exception>
    internal static async Task EnsureInitializedAsync(EdlSession session, string? programmerPath,
        EdlInitializationOptions? initialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(programmerPath) || !File.Exists(programmerPath))
        {
            await session.EnsureInitializedAsync(null, initialization, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (SaharaProgrammerManifest.IsManifestPath(programmerPath))
        {
            var imageSet = EdlProgrammerImageSet.Open(programmerPath);
            await EnsureInitializedAsync(session, imageSet, initialization, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var programmer = File.OpenRead(programmerPath);
        await session.EnsureInitializedAsync(programmer, initialization, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the session is initialized with the supplied pre-built programmer image set, e.g. a multi-image
    /// mapping inferred in memory when a folder carries no vendor manifest.
    /// <para>用所给预构建的 programmer 镜像集合确保会话已初始化，例如目录没有厂商清单时在内存推断出的多镜像映射。</para>
    /// </summary>
    /// <param name="session">The session to start; it is opened by the caller's device lease.
    /// <para>要启动的会话；由调用方的设备租约打开。</para></param>
    /// <param name="programmerImages">The validated image set to upload, or <see langword="null"/> to start
    /// without a programmer upload.
    /// <para>要上传的已校验镜像集合；为 <see langword="null"/> 时不带 programmer 上传启动。</para></param>
    /// <param name="initialization">Optional session startup requirements.
    /// <para>可选的会话启动要求。</para></param>
    /// <param name="cancellationToken">Cancels the parse and the handshake.
    /// <para>取消解析与握手。</para></param>
    /// <returns>A task that completes when the session is configured or was already configured.
    /// <para>会话已配置或本就已配置时完成的任务。</para></returns>
    /// <exception cref="InvalidDataException">One referenced image is missing or the set is not usable.
    /// <para>某个被引用的镜像缺失或集合不可用。</para></exception>
    /// <exception cref="IOException">A programmer image exists but cannot be opened.
    /// <para>programmer 镜像存在但无法打开。</para></exception>
    internal static async Task EnsureInitializedAsync(EdlSession session, EdlProgrammerImageSet? programmerImages,
        EdlInitializationOptions? initialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (programmerImages is null)
        {
            await session.EnsureInitializedAsync(null, initialization, cancellationToken).ConfigureAwait(false);
            return;
        }

        await session.EnsureInitializedFromImageSetAsync(programmerImages, initialization, cancellationToken)
            .ConfigureAwait(false);
    }
}