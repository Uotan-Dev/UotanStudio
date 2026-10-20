# UotanStudio.IconVerification

手动运行的 Avalonia Headless 图标验证宿主。它不在 `UotanStudio.slnx` 内，因此 `dotnet build/test --solution UotanStudio.slnx` 不会构建或运行它；需要验证图标时单独运行。

## 用途

- 用 `Avalonia.Headless` + Skia 真实渲染 `Irihi.Iconica.TDesign` 图标控件和 `AppIcon` 适配器。
- 自动检查每个图标的非空形状、边界/裁切、前景色占比、16/24/48/96 px 缩放比例，以及 24 px 间距是否被越过。
- 生成每个图标的 PNG、分组联系表（contact sheet）、间距/尺寸/前景色示意图，供逐项目视。
- 输出 `report.txt` 与 `summary.txt`；有失败时进程返回 1。

## 运行

```text
dotnet run --project UotanStudio.IconVerification/UotanStudio.IconVerification.csproj -c Debug
```

输出目录：`artifacts/icon-verification/`（已被 `.gitignore` 忽略）。

- `icons/`：每个图标的 96 px PNG。
- `sheets/static-*.png`、`sheets/appicon-*.png`：带标签的分组联系表。
- `sheets/spacing.png`：48 px 图标、24 px 间隔与 24 px 外边距。
- `sheets/sizes.png`：同一图标在 16/24/48/96 px 下的尺寸对比。
- `sheets/foreground.png`：红/蓝/绿前景与深色背景白色前景对比。
- `report.txt` / `summary.txt`：逐项结果与失败汇总。

## 图标来源

静态图标从 `UotanStudio/**/*.axaml` 中读取 `https://irihi.tech/iconica/tdesign` 命名空间下的标签；动态图标枚举 `AppIconKind` 的全部取值。两个集合都会渲染检查。
