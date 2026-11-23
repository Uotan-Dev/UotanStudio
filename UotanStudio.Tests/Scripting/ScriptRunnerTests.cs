using UotanStudio.Scripting;
using UotanStudio.Scripting.Apis;
using Xunit;

namespace UotanStudio.Tests.Scripting;

public class ScriptRunnerTests
{
    [Fact]
    public async Task RunAsync_WhitelistedFlow_ProducesBoundedOutputAndLogs()
    {
        const string source = """
            const flashed = JSON.parse(fastboot.flash('boot', '/tmp/boot.img'));
            log.info('flashed ' + flashed.partition);
            const km = unit.convert(1500, 'meter', 'kilometer');
            math.clamp(9, 0, 5) + km;
            """;

        var result = await ScriptRunner.RunAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("6.5", result.Output);
        Assert.Contains(result.Logs, entry => entry.Level == ScriptLogLevel.Info && entry.Message == "flashed boot");
    }

    [Fact]
    public async Task RunAsync_InfiniteLoop_FailsWithinTimeout()
    {
        var limits = new ScriptLimits
        {
            Timeout = TimeSpan.FromMilliseconds(200),
            MaxStatements = 1_000_000
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await ScriptRunner.RunAsync("while (true) {}", limits: limits,
            cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RunAsync_StatementLimit_Fails()
    {
        var limits = new ScriptLimits { MaxStatements = 50 };

        var result = await ScriptRunner.RunAsync("for (let i = 0; i < 10000; i++) {}", limits: limits,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunAsync_ClrAccess_IsRejected()
    {
        var result = await ScriptRunner.RunAsync("const file = System.IO.File; 'unreachable';",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunAsync_InvalidPartition_IsRejected()
    {
        var result = await ScriptRunner.RunAsync("fastboot.flash('../boot', '/tmp/boot.img');",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunAsync_UnsupportedUnit_IsRejected()
    {
        var result = await ScriptRunner.RunAsync("unit.convert(1, 'meter', 'megabyte');",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunAsync_DeclarativePanel_RejectsOversizedTree()
    {
        var limits = new ScriptLimits { MaxUiElements = 2 };
        const string source = """
            ui.showPanel(JSON.stringify({
                type: 'panel',
                children: [{type:'button'},{type:'button'},{type:'button'}]
            }));
            """;

        var result = await ScriptRunner.RunAsync(source, limits: limits,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RunAsync_UiConfirm_DispatchesToHost()
    {
        var ui = new RecordingUiApi();
        var context = new ScriptApiContext { Ui = ui };

        var result = await ScriptRunner.RunAsync("ui.confirm('Continue?') ? 'yes' : 'no';", context,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("yes", result.Output);
        Assert.Equal("Continue?", ui.Confirmation);
    }

    private sealed class RecordingUiApi : IUiScriptApi
    {
        public string? Confirmation { get; private set; }

        public bool Confirm(string message)
        {
            Confirmation = message;
            return true;
        }

        public void Alert(string message)
        {
        }

        public void ProgressStart(string title)
        {
        }

        public void ProgressUpdate(int percent)
        {
        }

        public void ProgressDone(string message)
        {
        }

        public string ShowPanel(string descriptionJson) => "{\"accepted\":true}";
    }
}