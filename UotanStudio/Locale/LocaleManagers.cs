using Irihi.Lingua;

namespace UotanStudio.Locale;

/// <summary>
/// Source-generated reactive locale manager for the common UI strings (tabs, dialogs, delivery,
/// flash, firmware fields, compare, explorer, user-error summaries). The Irihi.Lingua generator
/// fills one observable string property per key in <c>Locale/Common.resx</c> plus its culture variants.
/// <para>通用界面文本（选项卡、对话框、交付、烧录、固件字段、对比、文件管理、用户错误摘要）的源生成响应式语言管理器。Irihi.Lingua 生成器按 <c>Locale/Common.resx</c> 及其语言变体中的每个键生成一个可观测字符串属性。</para>
/// </summary>
[LinguaManager("./Locale/Common.resx")]
internal partial class CommonLocaleManager;

/// <summary>
/// Source-generated reactive locale manager for menu and keyboard-action strings.
/// <para>菜单与快捷键动作文本的源生成响应式语言管理器。</para>
/// </summary>
[LinguaManager("./Locale/Menu.resx")]
internal partial class MenuLocaleManager;

/// <summary>
/// Source-generated reactive locale manager for dock/workspace, properties panel and mirroring strings.
/// <para>停靠区/工作区、属性面板与投屏文本的源生成响应式语言管理器。</para>
/// </summary>
[LinguaManager("./Locale/Dock.resx")]
internal partial class DockLocaleManager;

/// <summary>
/// Source-generated reactive locale manager for node-editor strings.
/// <para>节点编辑器文本的源生成响应式语言管理器。</para>
/// </summary>
[LinguaManager("./Locale/Node.resx")]
internal partial class NodeLocaleManager;

/// <summary>
/// Source-generated reactive locale manager for settings-page strings.
/// <para>设置页文本的源生成响应式语言管理器。</para>
/// </summary>
[LinguaManager("./Locale/Settings.resx")]
internal partial class SettingsLocaleManager;

/// <summary>
/// Source-generated reactive locale manager for device and USB-tree strings.
/// <para>设备与 USB 树文本的源生成响应式语言管理器。</para>
/// </summary>
[LinguaManager("./Locale/Device.resx")]
internal partial class DeviceLocaleManager;

/// <summary>
/// Source-generated reactive locale manager for workflow interaction and validation strings.
/// <para>工作流交互与校验文本的源生成响应式语言管理器。</para>
/// </summary>
[LinguaManager("./Locale/Interaction.resx")]
internal partial class InteractionLocaleManager;
