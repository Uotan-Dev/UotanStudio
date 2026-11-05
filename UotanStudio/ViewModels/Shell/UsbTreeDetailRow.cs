namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// One rendered row of the USB tree detail panel: the localized entry label plus its value. Rows the
/// producing backend cannot report in principle are never emitted, so the panel only shows entries the
/// active backend actually provides.
/// <para>USB 树详情面板渲染的一行：本地化的条目标签与其取值。产出后端原理上无法上报的条目不会生成，
/// 因此面板只展示当前后端真正提供的条目。</para>
/// </summary>
/// <param name="Label">
/// The localized entry label. <para>本地化的条目标签。</para>
/// </param>
/// <param name="Value">
/// The localized value text, including the "none" wording for reported-but-empty values.
/// <para>本地化的取值文本，包括对已上报但为空的值使用的「无」措辞。</para>
/// </param>
public sealed record UsbTreeDetailRow(string Label, string Value);