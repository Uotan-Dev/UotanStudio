namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Provides helper methods to execute synchronous delegates on the thread pool with cancellation support.
/// <para>提供在线程池上执行同步委托并支持取消的辅助方法。</para>
/// </summary>
internal static class UsbAsyncExecution
{
    /// <summary>
    /// Runs a function on the thread pool and returns its result.
    /// <para>在线程池上运行一个函数并返回其结果。</para>
    /// </summary>
    /// <typeparam name="T">The return type of the function. <para>函数的返回类型。</para></typeparam>
    /// <param name="action">The function to execute. <para>要执行的函数。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous operation with the function result. <para>表示异步操作并包含函数结果的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested. <para>当请求取消时抛出。</para></exception>
    public static Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }, cancellationToken);
    }

    /// <summary>
    /// Runs an action on the thread pool without a return value.
    /// <para>在线程池上运行一个无返回值的操作。</para>
    /// </summary>
    /// <param name="action">The action to execute. <para>要执行的操作。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested. <para>当请求取消时抛出。</para></exception>
    public static Task Run(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }, cancellationToken);
    }
}