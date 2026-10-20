namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> that records every report into a list on the caller's thread.
/// <para>同步 <see cref="IProgress{T}"/>，在调用线程上把每次报告写入列表，便于测试同步断言进度。</para>
/// </summary>
/// <typeparam name="T">The reported value type. <para>上报值类型。</para></typeparam>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = new();

    /// <summary>Gets the reports received so far. <para>获取到目前为止收到的报告。</para></summary>
    public IReadOnlyList<T> Reports => _reports;

    /// <inheritdoc/>
    public void Report(T value) => _reports.Add(value);
}