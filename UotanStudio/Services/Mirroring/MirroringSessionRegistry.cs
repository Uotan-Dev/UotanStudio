using System.Collections.Concurrent;

namespace UotanStudio.Services.Mirroring;

/// <summary>
/// Host-scoped registry that lets the mirroring Start and Stop graph nodes share one session by id and
/// dispose any still-active sessions during shutdown.
/// <para>宿主级注册表：让投屏开始与停止图节点按 id 共享同一会话，并在关闭时释放仍处于活动状态的会话。</para>
/// </summary>
internal static class MirroringSessionRegistry
{
    private static readonly ConcurrentDictionary<string, ManagedMirrorSession> Sessions = new(StringComparer.Ordinal);

    /// <summary>Registers one session and returns the generated identifier used by the Stop node.
    /// <para>注册一个会话并返回供停止节点使用的生成标识。</para></summary>
    public static string Add(ManagedMirrorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var id = Guid.NewGuid().ToString("N");
        Sessions[id] = session;
        return id;
    }

    /// <summary>Removes and returns the session registered under the given identifier.
    /// <para>移除并返回以给定标识注册的会话。</para></summary>
    public static bool TryTake(string id, out ManagedMirrorSession? session)
        => Sessions.TryRemove(id, out session);

    /// <summary>Disposes every registered session, used on host shutdown and by tests for isolation.
    /// <para>释放所有已注册会话，供宿主关闭与测试隔离使用。</para></summary>
    public static void DisposeAll()
    {
        foreach (var id in Sessions.Keys)
        {
            if (Sessions.TryRemove(id, out var session))
            {
                session.Dispose();
            }
        }
    }
}