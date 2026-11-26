# 语法文件来源与许可

本目录的 TextMate 语法由 `UotanStudio.csproj` 的 `TintGrammar` 项在构建期交给 Tint 源生成器，
编译进 `UotanStudio.dll`（根命名空间 `UotanStudio`）。Tint 内置的 VS Code 语法不包含设备树、
TOML 与 x86 汇编，因此这三份语法在此单独维护。

## dts.tmLanguage.json

设备树语法（scope `source.dts`），取自 VS Code 扩展 **plorefice.devicetree**，按 MIT 许可再分发。

- 上游：https://github.com/plorefice/vscode-devicetree
- 版权：Copyright (c) 2017 Pietro Lorefice
- 许可：MIT

相对上游的改动：`fileTypes` 增加 `dtso`；新增预处理指令匹配（`#define`/`#if*` 等）与属性名匹配；
仓库内括号类作用域由 `*.go` 更正为 `*.dts`。

~~~text
MIT License

Copyright (c) 2017 Pietro Lorefice

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
~~~

## toml.tmLanguage.json

TOML 1.0 语法（scope `source.toml`），为本项目编写，未再分发任何上游语法。

## asm-x86.tmLanguage.json

x86/x86-64 汇编语法（scope `source.asm.x86`），覆盖 GAS、Intel 与 NASM 写法，为本项目编写，
未再分发任何上游语法。
