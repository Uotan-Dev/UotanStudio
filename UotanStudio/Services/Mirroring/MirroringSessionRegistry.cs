using System.Collections.Concurrent;

namespace UotanStudio.Services.Mirroring;

/// <summary>
/// Registry letting the mirroring Start and Stop graph nodes share one session by id and
/// dispose still-active sessions during shutdown.
/// <para>注册表：让投屏开始与停止图节点按 id 共享同一会话，并在关闭时释放仍处于活动状态的会话。</para>
/// </summary>
internal static class MirroringSessionRegistry
{
    private static readonly ConcurrentDictionary<string, ManagedMirrorSession> Sessions = new(StringComparer.Ordinal);

    /// <summary>Registers one session and returns the generated identifier.
    /// <para>注册一个会话并返回生成的标识。</para></summary>
    /// <param name="session">Session to register.<para>要注册的会话。</para></param>
    /// <returns>The freshly generated identifier.<para>新生成的标识。</para></returns>
    public static string Add(ManagedMirrorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var id = Guid.NewGuid().ToString("N");
        Sessions[id] = session;
        return id;
    }

    /// <summary>Removes and returns the session registered under the given identifier.
    /// <para>移除并返回以给定标识注册的会话。</para></summary>
    /// <param name="id">Identifier to look up.<para>要查询的标识。</para></param>
    /// <param name="session">Removed session, when the identifier was present.<para>标识存在时被移除的会话。</para></param>
    /// <returns>True when a session was removed.<para>成功移除会话时为 true。</para></returns>
    public static bool TryTake(string id, out ManagedMirrorSession? session)
        => Sessions.TryRemove(id, out session);

    /// <summary>Disposes every registered session, leaving the registry empty.
    /// <para>释放所有已注册会话，使注册表恢复为空。</para></summary>
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