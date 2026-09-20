using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Providers;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Stores and creates registered USB API providers.
/// <para>存储并创建已注册的 USB API 提供器。</para>
/// </summary>
public sealed class UsbApiRegistry
{
    private readonly Dictionary<string, Func<IUsbApiProvider>> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Occurs when a provider is registered.
    /// <para>当提供器注册时触发。</para>
    /// </summary>
    public event Action<IUsbApiProvider>? ProviderRegistered;

    /// <summary>
    /// Occurs when an API name is registered.
    /// <para>当 API 名称注册时触发。</para>
    /// </summary>
    public event Action<string>? ProviderNameRegistered;

    /// <summary>
    /// Registers a provider factory under the specified API name.
    /// <para>在指定 API 名称下注册提供器工厂。</para>
    /// </summary>
    /// <param name="apiName">The API name. <para>API 名称。</para></param>
    /// <param name="factory">The provider factory. <para>提供器工厂方法。</para></param>
    public void Register(string apiName, Func<IUsbApiProvider> factory)
    {
        if (string.IsNullOrWhiteSpace(apiName))
        {
            throw new ArgumentException("API name cannot be null or whitespace.", nameof(apiName));
        }

        ArgumentNullException.ThrowIfNull(factory);

        _factories.Remove(apiName);
        _factories[apiName] = factory;
        ProviderNameRegistered?.Invoke(apiName);
        ProviderRegistered?.Invoke(new RegisteredProviderPlaceholder(apiName));
    }

    /// <summary>
    /// Tries to create a provider by name.
    /// <para>按名称尝试创建提供器。</para>
    /// </summary>
    /// <param name="apiName">The API name. <para>API 名称。</para></param>
    /// <param name="provider">The provider instance when successful. <para>成功时返回的提供器实例。</para></param>
    /// <returns><c>true</c> if a provider was created; otherwise, <c>false</c>. <para>创建成功返回 <c>true</c>，否则返回 <c>false</c>。</para></returns>
    public bool TryCreate(string apiName, out IUsbApiProvider? provider)
    {
        provider = null;
        if (!_factories.TryGetValue(apiName, out var factory))
        {
            return false;
        }

        provider = factory();
        return true;
    }

    /// <summary>
    /// Creates every registered provider.
    /// <para>创建所有已注册的提供器实例。</para>
    /// </summary>
    /// <returns>A read-only list of providers. <para>提供器只读列表。</para></returns>
    public IReadOnlyList<IUsbApiProvider> CreateAll() => [.. _factories.Values.Select(factory => factory())];

    /// <summary>
    /// Gets the registered API names.
    /// <para>获取已注册的 API 名称列表。</para>
    /// </summary>
    /// <returns>A read-only list of names. <para>名称只读列表。</para></returns>
    public IReadOnlyList<string> GetApiNames() => [.. _factories.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Creates the default registry with native, libusb and harmony providers.
    /// <para>创建包含 native、libusb 与 harmony 提供器的默认注册表。</para>
    /// </summary>
    /// <returns>The default registry. <para>默认注册表实例。</para></returns>
    public static UsbApiRegistry CreateDefault()
    {
        var registry = new UsbApiRegistry();
        registry.Register(NativeUsbApiProvider.ApiNameConst, () => new NativeUsbApiProvider());
        registry.Register(LibUsbApiProvider.ApiNameConst, () => new LibUsbApiProvider());
        registry.Register(HarmonyOSUsbApiProvider.ApiNameConst, () => new HarmonyOSUsbApiProvider());
#if ANDROID
        registry.Register(AndroidUsbApiProvider.ApiNameConst, () => new AndroidUsbApiProvider());
#endif
        return registry;
    }

    private sealed class RegisteredProviderPlaceholder(string apiName) : IUsbApiProvider
    {
        public string ApiName { get; } = apiName;

        public UsbApiKind ApiKind => UsbApiKind.Custom;

        public bool IsSupportedOnCurrentPlatform => true;

        public IReadOnlyList<IUsbDeviceSession> EnumerateDeviceSessions(UsbDeviceFilter? filter = null) => [];

        public IReadOnlyList<UsbDeviceInfo> EnumerateDeviceInfos(UsbDeviceFilter? filter = null) => [];

        public UsbApiCapabilities GetCapabilities() => new()
        {
            ApiName = ApiName,
            ApiKind = ApiKind,
            IsSupportedOnCurrentPlatform = IsSupportedOnCurrentPlatform
        };
    }
}