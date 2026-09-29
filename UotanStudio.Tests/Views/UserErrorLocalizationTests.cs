using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class UserErrorLocalizationTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task Resources_BothLanguages_HaveMatchingKeysAndFormatArguments()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var english = new UotanStudio.Locale.EnUs.Common();
                var chinese = new UotanStudio.Locale.ZhCn.Common();
                var keys = english.Keys.OfType<string>().Where(key => key.StartsWith("UserError_", StringComparison.Ordinal)).Order().ToArray();
                Assert.NotEmpty(keys);
                Assert.Equal(keys, chinese.Keys.OfType<string>().Where(key => key.StartsWith("UserError_", StringComparison.Ordinal)).Order());
                foreach (var key in keys)
                {
                    var en = Assert.IsType<string>(english[key]);
                    var zh = Assert.IsType<string>(chinese[key]);
                    Assert.False(string.IsNullOrWhiteSpace(en));
                    Assert.False(string.IsNullOrWhiteSpace(zh));
                    Assert.NotEqual(en, zh);
                    Assert.Equal(CompositeFormat.Parse(en).MinimumArgumentCount, CompositeFormat.Parse(zh).MinimumArgumentCount);
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task Resources_BothLanguages_OpenFailureKeysMatchAndFormatArgumentsAlign()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var english = new UotanStudio.Locale.EnUs.Menu();
                var chinese = new UotanStudio.Locale.ZhCn.Menu();
                var keys = english.Keys.OfType<string>().Where(key => key.StartsWith("Menu_Open", StringComparison.Ordinal)).Order().ToArray();
                Assert.Contains("Menu_OpenProjectMissing", keys);
                Assert.Contains("Menu_OpenSolutionUnsupported", keys);
                Assert.Contains("Menu_OpenNodeProjectUnsupported", keys);
                Assert.Equal(keys, chinese.Keys.OfType<string>().Where(key => key.StartsWith("Menu_Open", StringComparison.Ordinal)).Order());
                foreach (var key in keys)
                {
                    var en = Assert.IsType<string>(english[key]);
                    var zh = Assert.IsType<string>(chinese[key]);
                    Assert.False(string.IsNullOrWhiteSpace(en));
                    Assert.False(string.IsNullOrWhiteSpace(zh));
                    Assert.NotEqual(en, zh);
                    Assert.Equal(CompositeFormat.Parse(en).MinimumArgumentCount, CompositeFormat.Parse(zh).MinimumArgumentCount);
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task Resources_BothLanguages_FlashKeysMatchAndFormatArgumentsAlign()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var english = new UotanStudio.Locale.EnUs.Common();
                var chinese = new UotanStudio.Locale.ZhCn.Common();
                var keys = english.Keys.OfType<string>().Where(key => key.StartsWith("Flash_", StringComparison.Ordinal)).Order().ToArray();
                Assert.NotEmpty(keys);
                Assert.Equal(keys, chinese.Keys.OfType<string>().Where(key => key.StartsWith("Flash_", StringComparison.Ordinal)).Order());
                foreach (var key in keys)
                {
                    var en = Assert.IsType<string>(english[key]);
                    var zh = Assert.IsType<string>(chinese[key]);
                    Assert.False(string.IsNullOrWhiteSpace(en));
                    Assert.False(string.IsNullOrWhiteSpace(zh));
                    Assert.NotEqual(en, zh);
                    Assert.Equal(CompositeFormat.Parse(en).MinimumArgumentCount, CompositeFormat.Parse(zh).MinimumArgumentCount);
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task Describe_LanguageSwitch_LocalizesReasonAndPreservesOriginalException()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var language = LanguageService.Instance;
                var originalCulture = language.CurrentLanguage;
                language.Initialize();
                (Exception Error, string Key)[] cases =
                [
                    (new FileNotFoundException("missing-original {0}", "original.bin"), "UserError_NotFound"),
                    (new UnauthorizedAccessException("permission-original"), "UserError_AccessDenied"),
                    (new InvalidDataException("parser-original"), "UserError_InvalidData"),
                    (new SocketException((int)SocketError.ConnectionRefused), "UserError_ConnectionRefused"),
                    (new TimeoutException("timeout-original"), "UserError_Timeout"),
                    (new OperationCanceledException("cancel-original"), "UserError_Cancelled"),
                    (new CryptographicException("signature-original"), "UserError_Cryptography"),
                    (new NotSupportedException("unsupported-original"), "UserError_NotSupported"),
                    (new ArgumentException("argument-original", "path"), "UserError_InvalidInput"),
                    (new IOException("io-original"), "UserError_Io"),
                    (new Exception("unknown-original", new IOException("inner-original")), "UserError_OperationFailed")
                ];
                try
                {
                    foreach (var culture in new[] { "zh-CN", "en-US" })
                    {
                        language.ApplyLanguage(culture);
                        foreach (var (error, key) in cases)
                        {
                            var original = error.ToString();
                            var display = UserError.Describe(error);
                            Assert.StartsWith(language.GetString(key), display, StringComparison.Ordinal);
                            Assert.Contains(language.GetString("UserError_Details"), display, StringComparison.Ordinal);
                            Assert.EndsWith(error.Message, display, StringComparison.Ordinal);
                            Assert.Equal(original, error.ToString());
                        }
                        var formatted = UserError.Describe(new InvalidDataException("raw {1}"), "UserError_ParseFailed", "DEX");
                        Assert.Contains("DEX", formatted, StringComparison.Ordinal);
                        Assert.EndsWith("raw {1}", formatted, StringComparison.Ordinal);
                        Assert.DoesNotContain("{0}", formatted, StringComparison.Ordinal);
                    }
                }
                finally
                {
                    language.ApplyLanguage(originalCulture);
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task LoadContentAsync_MissingFile_LocalizesVisibleErrorAndKeepsDiagnosticLog()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(async () =>
            {
                var language = LanguageService.Instance;
                var originalCulture = language.CurrentLanguage;
                var entries = new ConcurrentQueue<LogEntry>();
                var unsubscribe = LogService.Instance.Subscribe((_, entry) => entries.Enqueue(entry));
                var path = Path.Combine(Path.GetTempPath(), "uotan-i18n-missing-" + Guid.NewGuid().ToString("N"), "file.bin");
                language.Initialize();
                try
                {
                    foreach (var culture in new[] { "zh-CN", "en-US" })
                    {
                        language.ApplyLanguage(culture);
                        var document = new FileViewModel();
                        await document.LoadContentAsync(path);
                        Assert.StartsWith(language.GetString("UserError_NotFound"), document.CompileError, StringComparison.Ordinal);
                        Assert.Contains(language.GetString("UserError_Details"), document.CompileError, StringComparison.Ordinal);
                        Assert.Contains(path, document.CompileError, StringComparison.Ordinal);
                    }
                    Assert.Contains(entries, entry => entry.Source == "FileEditor" && entry.Message.StartsWith("Failed to load", StringComparison.Ordinal));
                    Assert.DoesNotContain(entries, entry => entry.Source == "FileEditor" && entry.Message.Contains("错误详情", StringComparison.Ordinal));
                }
                finally
                {
                    unsubscribe();
                    language.ApplyLanguage(originalCulture);
                }
            }, token);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }
}