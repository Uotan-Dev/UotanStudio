namespace UotanStudio.Comm.Transports.Abstractions;

/// <summary>
/// Optional transport capability for links whose line rate can be changed while connected.
/// <para>可选传输能力：已连接期间可以改变线路速率的链路。</para>
/// </summary>
/// <remarks>
/// Only serial links implement this. Protocol code that negotiates a new rate must obtain it with a
/// capability query and treat its absence as "the host cannot follow the agent", never as a no-op:
/// an agent that has already switched its own rate stops answering the old one.
/// <para>只有串口链路实现本接口。协商新速率的协议代码必须经能力查询获取它，并把缺失视为
/// “主机无法跟随代理”，绝不能当作空操作：已经切换自身速率的代理不再回应旧速率。</para>
/// </remarks>
public interface ICommTransportBaudRateControl
{
    /// <summary>Gets the baud rate currently applied to the open link.
    /// <para>获取当前应用到已连接链路的波特率。</para></summary>
    /// <value>The live rate, which differs from the construction rate after a successful switch.
    /// <para>生效中的速率；成功切换后与会与构造时不同。</para></value>
    int BaudRate { get; }

    /// <summary>Applies a new baud rate to the open link.
    /// <para>对已连接链路应用新的波特率。</para></summary>
    /// <param name="baudRate">The positive target rate.<para>正的目标速率。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.
    /// <para>速率非正。</para></exception>
    /// <exception cref="InvalidOperationException">The link is not open.
    /// <para>链路未打开。</para></exception>
    void SetBaudRate(int baudRate);
}