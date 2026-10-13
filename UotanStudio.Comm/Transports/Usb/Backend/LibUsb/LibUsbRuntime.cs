namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// The public entry point that lets a host register the native library it downloaded for the libusb backend.
/// The binaries no longer ship with the repository, so the host registers the prepared file here; opening the
/// device handle itself stays in the Comm transport layer.
/// <para>公开入口，供宿主为 libusb 后端注册运行时下载得到的原生库。库二进制不再随仓库分发，宿主在准备阶段
/// 取得文件后经此处完成注册；直接打开设备句柄的仍是 Comm 的传输层。</para>
/// </summary>
/// <remarks>
/// The native binary has no single managed loader, so the host must not guess the file name or the platform
/// subdirectory: it registers exactly the absolute path it resolved and verified.
/// <para>原生库没有统一的托管加载器，因此宿主不应猜测文件名或平台子目录：它只注册自己解析并校验过的绝对路径。</para>
/// </remarks>
public static class LibUsbRuntime
{
    /// <summary>
    /// Gets the absolute path of the native library the host registered; <see langword="null"/> when none was
    /// registered.
    /// <para>获取调用者注册的原生库绝对路径；未注册时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The configured absolute path, or <see langword="null"/>.
    /// <para>已配置的绝对路径；未配置时为 <see langword="null"/>。</para></value>
    public static string? LibraryPath => LibUsbNativeLoader.LibraryPath;

    /// <summary>
    /// Registers the absolute path of the native libusb runtime and pre-loads it so the bare SONAME resolves.
    /// Throws <see cref="FileNotFoundException"/> when the file does not exist; registration must happen before
    /// the first native call, and a later path change does not take effect.
    /// <para>注册原生 libusb 运行时的绝对路径并预加载，使裸 SONAME 可解析。文件不存在时抛
    /// <see cref="FileNotFoundException"/>；注册在首次原生调用前完成，之后再改路径不生效。</para>
    /// </summary>
    /// <param name="libraryPath">Absolute path to <c>libusb-1.0.dylib</c>, <c>libusb-1.0.so</c> or
    /// <c>libusb-1.0.dll</c>. <para><c>libusb-1.0.dylib</c>、<c>libusb-1.0.so</c> 或
    /// <c>libusb-1.0.dll</c> 的绝对路径。</para></param>
    /// <exception cref="ArgumentException"><paramref name="libraryPath"/> is blank.
    /// <para><paramref name="libraryPath"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    public static void SetLibraryPath(string libraryPath)
        => LibUsbNativeLoader.SetLibraryPath(libraryPath);
}