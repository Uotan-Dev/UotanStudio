# UotanStudio.Benchmarks 项目指令

在本目录内新增基准、剖析运行器或性能结论前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目是独立的 BenchmarkDotNet 控制台程序，不在 `UotanStudio.slnx` 内，解决方案构建、format 与测试门都不会碰它；验证必须单独运行。
- 它引用 `Firmware`、`Hash`、`Compress`、`Comm` 与宿主 `UotanStudio`，用于基准、启动测量与 headless UI 夹具；这些引用只服务测量，不得把测量专用类型或开关反向加到生产项目。
- `Avalonia.Headless` 的 `PackageVersion` 必须留在 `Directory.Packages.props`；删掉后本项目的 `dotnet restore` 会以 `NU1010` 失败，而解决方案门仍然全绿。
- `UotanStudio.Benchmarks.csproj` 的 `CS0618` 抑制只用于旧的整镜像内存型构建 API；生产调用方仍应使用流式重载。

## 运行方式

- 普通基准：`dotnet run -c Release --project UotanStudio.Benchmarks -- --filter <BenchmarkName>`。
- 运行时剖析：`--profile --workload <name> [--repeats N] [--files N] [--megabytes N] [--out <path>]`。当前工作负载包括 `erofs-zstd-pack`、`erofs-zstd-unpack`、`erofs-lz4-pack`、`erofs-lz4-unpack`、`erofs-lzma-pack`、`erofs-lzma-unpack` 与 `ext4-*`。
- 真实镜像：`--image-run <facts|read|read-parallel|extract|pack|compare|all> --image <path> [--work <dir>] [--worker-counts 1,8] [--no-compress]`；`pack` 依赖先前的 `extract` 目录，`compare` 需要 `--rebuilt`。
- 两种运行器都会输出镜像 SHA-256。涉及构建输出的改动要与改动前逐字节比较，不能只看耗时。

## 测量纪律

- 只在 Release 下测；比较前后使用同一台机器、同一输入、同一参数与同样的并发背景。不要在有构建、测试或其它基准同时运行时给出对比结论。
- `RuntimeProfile` 的订阅本身有开销；只有同一记录器的两次运行可以互相比，剖析绝对值不能与未剖析运行直接比较。
- 修改生产代码与运行基准要分开提交或至少分开运行；不要用基准结果掩盖行为变化，也不要把一次噪声波动写成稳定改进。
- 基准日志与输出放在 `artifacts/` 或 `--out` 指定位置；提交前确认没有把大镜像、语料或临时目录带进仓库。

## 验证

```text
dotnet restore UotanStudio.Benchmarks/UotanStudio.Benchmarks.csproj
dotnet build UotanStudio.Benchmarks/UotanStudio.Benchmarks.csproj --no-restore --nologo -c Release
dotnet run -c Release --project UotanStudio.Benchmarks -- --profile --workload erofs-zstd-pack --repeats 1
```

- 本项目没有自动化正确性门禁；基准通过不等于功能正确，性能数字也不能替代定向测试与真实设备/镜像验证。