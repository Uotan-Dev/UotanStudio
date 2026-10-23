namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Role a flow output port plays for the engine's control-flow dispatch. The engine reads these roles instead of
/// matching type keys, so a third-party control-flow node declared with the same roles receives identical
/// skip and loop semantics without any engine change.
/// <para>流输出端口在引擎控制流分派中承担的角色。引擎读取这些角色而不是匹配类型键，因此以相同角色声明的第三方
/// 控制流节点无需改动引擎即可获得相同的跳过与循环语义。</para>
/// </summary>
public enum NodeFlowRole
{
    /// <summary>No control-flow specialisation; the port only carries execution onward. <para>无控制流特化，端口仅把执行顺序传下去。</para></summary>
    None = 0,

    /// <summary>Taken when the node's condition evaluates true. <para>条件为真时被选中的分支。</para></summary>
    True = 1,

    /// <summary>Taken when the node's condition evaluates false. <para>条件为假时被选中的分支。</para></summary>
    False = 2,

    /// <summary>Repeated body of a loop. <para>循环被重复执行的循环体。</para></summary>
    Body = 3,

    /// <summary>Continuation after a loop finishes. <para>循环结束后的后续路径。</para></summary>
    Completed = 4
}

/// <summary>
/// Control-flow behaviour a node declares for the engine, independent of its type key. A node without any
/// declaration keeps the plain downstream-propagation behaviour.
/// <para>节点向引擎声明的控制流行为，与其类型键无关。未声明的节点保持普通的向下游传播语义。</para>
/// </summary>
public enum NodeFlowKind
{
    /// <summary>The node does not alter control flow beyond propagating written output values. <para>节点除传播输出值外不改变控制流。</para></summary>
    None = 0,

    /// <summary>Chooses between the <see cref="NodeFlowRole.True"/> and <see cref="NodeFlowRole.False"/> outputs. <para>在真、假两个输出之间择一。</para></summary>
    Branch = 1,

    /// <summary>Repeats the <see cref="NodeFlowRole.Body"/> output and then continues through <see cref="NodeFlowRole.Completed"/>. <para>重复循环体输出，随后经完成输出继续。</para></summary>
    Loop = 2,

    /// <summary>Runs the branches of both flow outputs concurrently. <para>并行执行两个流输出的分支。</para></summary>
    Parallel = 3
}