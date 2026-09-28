using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Reflection guard that fails when a concrete format handler exists in the Firmware assembly but
/// is never published by <see cref="FirmwareFormatRegistry.RegisterAll"/>, so new handlers cannot be
/// written without being wired into detection.
/// <para>反射守卫用例：当 Firmware 程序集中存在具体格式处理器却未被 <see cref="FirmwareFormatRegistry.RegisterAll"/>
/// 发布时使测试失败，从而防止新写的处理器未接入检测链路。</para>
/// </summary>
public class FirmwareFormatRegistryTests
{
    private static readonly Type[] IntentionallyUnregisteredHandlers =
    [
    ];

    [Fact]
    public void FirmwareFormatRegistry_RegistersEveryBundledHandler()
    {
        FirmwareFormatRegistry.RegisterAll();

        var assembly = typeof(FirmwareFormatRegistry).Assembly;
        var bundledHandlers = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsNested: false }
                        && typeof(IReadonlyFormatHandler).IsAssignableFrom(t))
            .ToHashSet();

        var registeredHandlers = FormatDetector.RegisteredHandlers
            .Select(h => h.GetType())
            .ToHashSet();

        var unregistered = bundledHandlers
            .Except(IntentionallyUnregisteredHandlers)
            .Except(registeredHandlers)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unregistered.Length == 0,
            "Handler types implement IReadonlyFormatHandler but are not registered: "
            + string.Join(", ", unregistered.Select(t => t!.FullName)));

        var phantom = registeredHandlers
            .Except(bundledHandlers)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.True(phantom.Length == 0,
            "Registered instances are not concrete bundled handler types: "
            + string.Join(", ", phantom.Select(t => t!.FullName)));
    }
}