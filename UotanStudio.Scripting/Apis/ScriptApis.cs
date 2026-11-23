using System.Text.Json;

namespace UotanStudio.Scripting.Apis;

/// <summary>
/// Sink receiving script log entries.
/// <para>接收脚本日志条目的接收器。</para>
/// </summary>
public interface ILogSink
{
    /// <summary>
    /// Records one bounded log entry.
    /// <para>记录一条有界日志。</para>
    /// </summary>
    /// <param name="level">Severity of the entry.<para>条目级别。</para></param>
    /// <param name="message">Already length-checked message text.<para>已做长度检查的消息文本。</para></param>
    void Write(ScriptLogLevel level, string message);
}

/// <summary>
/// Controlled fastboot surface exposed to scripts. Implementations belong to the host.
/// <para>暴露给脚本的受控 fastboot 能力面；实现由宿主提供。</para>
/// </summary>
public interface IFastbootScriptApi
{
    /// <summary>Flashes one image and returns a JSON result.<para>刷入一个镜像并返回 JSON 结果。</para></summary>
    /// <param name="partition">Validated partition name.<para>已校验的分区名。</para></param>
    /// <param name="imagePath">Validated absolute image path.<para>已校验的镜像绝对路径。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string Flash(string partition, string imagePath);

    /// <summary>Reboots the device and returns a JSON result.<para>重启设备并返回 JSON 结果。</para></summary>
    /// <param name="mode">Validated reboot mode.<para>已校验的重启模式。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string Reboot(string mode);

    /// <summary>Reads one device variable and returns a JSON result.<para>读取一个设备变量并返回 JSON 结果。</para></summary>
    /// <param name="name">Validated variable name.<para>已校验的变量名。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string GetVar(string name);

    /// <summary>Switches the active slot and returns a JSON result.<para>切换活动槽位并返回 JSON 结果。</para></summary>
    /// <param name="slot">Validated slot name.<para>已校验的槽位名。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string SetActiveSlot(string slot);
}

/// <summary>
/// Controlled image-inspection surface exposed to scripts.
/// <para>暴露给脚本的受控镜像检视能力面。</para>
/// </summary>
public interface IImageScriptApi
{
    /// <summary>Parses payload metadata and returns a JSON result.<para>解析 payload 元数据并返回 JSON 结果。</para></summary>
    /// <param name="path">Validated absolute image path.<para>已校验的镜像绝对路径。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string ParsePayload(string path);

    /// <summary>Parses super-image metadata and returns a JSON result.<para>解析 super 镜像元数据并返回 JSON 结果。</para></summary>
    /// <param name="path">Validated absolute image path.<para>已校验的镜像绝对路径。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string ParseSuper(string path);

    /// <summary>Reads one partition descriptor and returns a JSON result.<para>读取一个分区描述并返回 JSON 结果。</para></summary>
    /// <param name="path">Validated absolute image path.<para>已校验的镜像绝对路径。</para></param>
    /// <param name="partition">Validated partition name.<para>已校验的分区名。</para></param>
    /// <returns>JSON string describing the outcome.<para>描述结果的 JSON 字符串。</para></returns>
    string GetPartitionInfo(string path, string partition);
}

/// <summary>
/// Controlled UI surface exposed to scripts. All calls are host-dispatched.
/// <para>暴露给脚本的受控 UI 能力面；所有调用由宿主调度。</para>
/// </summary>
public interface IUiScriptApi
{
    /// <summary>Asks the user for confirmation and returns whether it was granted.<para>询问用户确认并返回是否同意。</para></summary>
    /// <param name="message">Validated prompt text.<para>已校验的提示文本。</para></param>
    /// <returns><see langword="true"/> when the user confirms.<para>用户确认时为 <see langword="true"/>。</para></returns>
    bool Confirm(string message);

    /// <summary>Shows a non-blocking notification.<para>显示非阻塞通知。</para></summary>
    /// <param name="message">Validated notification text.<para>已校验的通知文本。</para></param>
    void Alert(string message);

    /// <summary>Starts one bounded progress operation.<para>开始一个有界进度操作。</para></summary>
    /// <param name="title">Validated progress title.<para>已校验的进度标题。</para></param>
    void ProgressStart(string title);

    /// <summary>Updates the active progress operation.<para>更新当前进度操作。</para></summary>
    /// <param name="percent">Percentage in the inclusive range 0 through 100.<para>0 到 100 之间的百分比。</para></param>
    void ProgressUpdate(int percent);

    /// <summary>Completes the active progress operation.<para>完成当前进度操作。</para></summary>
    /// <param name="message">Validated completion text.<para>已校验的完成文本。</para></param>
    void ProgressDone(string message);

    /// <summary>
    /// Validates and accepts one declarative panel description.
    /// <para>校验并接受一个声明式面板描述。</para>
    /// </summary>
    /// <param name="descriptionJson">JSON text with a bounded element tree.<para>带元素数量上限的 JSON 文本。</para></param>
    /// <returns>JSON string describing the accepted panel.<para>描述已接受面板的 JSON 字符串。</para></returns>
    string ShowPanel(string descriptionJson);
}

/// <summary>
/// Injection point for host capabilities available to one script execution.
/// <para>一次脚本执行可用的宿主能力注入点。</para>
/// </summary>
public sealed class ScriptApiContext
{
    /// <summary>Log sink receiving all script log calls.<para>接收全部脚本日志调用的接收器。</para></summary>
    public ILogSink Log { get; init; } = new CollectingLogSink();

    /// <summary>Fastboot surface; defaults to a simulated device.<para>fastboot 能力面；默认为模拟设备。</para></summary>
    public IFastbootScriptApi Fastboot { get; init; } = new SimulatedFastbootScriptApi();

    /// <summary>Image-inspection surface; defaults to simulated metadata.<para>镜像检视能力面；默认为模拟元数据。</para></summary>
    public IImageScriptApi Image { get; init; } = new SimulatedImageScriptApi();

    /// <summary>UI surface; defaults to a no-op implementation.<para>UI 能力面；默认空实现。</para></summary>
    public IUiScriptApi Ui { get; init; } = new NoOpUiScriptApi();
}

/// <summary>
/// In-memory log sink with a fixed entry budget.
/// <para>具有固定条目预算的内存日志接收器。</para>
/// </summary>
public sealed class CollectingLogSink : ILogSink
{
    private readonly List<ScriptLogEntry> _entries = [];

    /// <summary>Creates a sink with the supplied entry budget.<para>创建具有指定条目预算的接收器。</para></summary>
    /// <param name="limit">Maximum retained entries; negative values are clamped to zero.<para>保留条目的上限；负值按零处理。</para></param>
    public CollectingLogSink(int limit = 200) => Limit = Math.Max(0, limit);

    /// <summary>Maximum number of retained entries.<para>保留条目的最大数量。</para></summary>
    public int Limit { get; set; }

    /// <summary>Retained log entries in emission order.<para>按产生顺序保留的日志条目。</para></summary>
    public IReadOnlyList<ScriptLogEntry> Entries => _entries;

    /// <inheritdoc/>
    public void Write(ScriptLogLevel level, string message)
    {
        if (_entries.Count >= Limit)
        {
            return;
        }

        _entries.Add(new ScriptLogEntry(level, message));
    }
}

/// <summary>
/// Simulated fastboot device used by script previews and tests.
/// <para>用于脚本预览与测试的模拟 fastboot 设备。</para>
/// </summary>
public sealed class SimulatedFastbootScriptApi : IFastbootScriptApi
{
    /// <inheritdoc/>
    public string Flash(string partition, string imagePath) =>
        $"{{\"ok\":true,\"partition\":\"{JsonText.Escape(partition)}\",\"imagePath\":\"{JsonText.Escape(imagePath)}\"}}";

    /// <inheritdoc/>
    public string Reboot(string mode) =>
        $"{{\"ok\":true,\"mode\":\"{JsonText.Escape(mode)}\"}}";

    /// <inheritdoc/>
    public string GetVar(string name) =>
        $"{{\"name\":\"{JsonText.Escape(name)}\",\"value\":\"simulated\"}}";

    /// <inheritdoc/>
    public string SetActiveSlot(string slot) =>
        $"{{\"ok\":true,\"slot\":\"{JsonText.Escape(slot)}\"}}";
}

/// <summary>
/// Simulated image inspector used by script previews and tests.
/// <para>用于脚本预览与测试的模拟镜像检视器。</para>
/// </summary>
public sealed class SimulatedImageScriptApi : IImageScriptApi
{
    /// <inheritdoc/>
    public string ParsePayload(string path) =>
        $"{{\"path\":\"{JsonText.Escape(path)}\",\"partitions\":[{{\"name\":\"boot\",\"size\":1048576}}]}}";

    /// <inheritdoc/>
    public string ParseSuper(string path) =>
        $"{{\"path\":\"{JsonText.Escape(path)}\",\"partitions\":[{{\"name\":\"system\",\"size\":268435456}}]}}";

    /// <inheritdoc/>
    public string GetPartitionInfo(string path, string partition) =>
        $"{{\"path\":\"{JsonText.Escape(path)}\",\"partition\":\"{JsonText.Escape(partition)}\",\"offset\":0,\"size\":1048576}}";
}

/// <summary>
/// No-op UI surface used when a host has not installed one.
/// <para>宿主未安装 UI 能力面时使用的空实现。</para>
/// </summary>
public sealed class NoOpUiScriptApi : IUiScriptApi
{
    /// <inheritdoc/>
    public bool Confirm(string message) => true;

    /// <inheritdoc/>
    public void Alert(string message)
    {
    }

    /// <inheritdoc/>
    public void ProgressStart(string title)
    {
    }

    /// <inheritdoc/>
    public void ProgressUpdate(int percent)
    {
    }

    /// <inheritdoc/>
    public void ProgressDone(string message)
    {
    }

    /// <inheritdoc/>
    public string ShowPanel(string descriptionJson) => "{\"accepted\":true,\"elements\":0}";
}

internal static class JsonText
{
    internal static string Escape(string value) => JsonEncodedText.Encode(value).ToString();
}