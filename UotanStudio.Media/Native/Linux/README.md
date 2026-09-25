# Linux VA-API 解码后端

`VaApiVideoDecoder` 经固定 C ABI 的 `libuotan_media_va.so` 调用 libavcodec 的 VA-API
硬件后端。libavcodec 负责 H.264/HEVC/AV1 码流语法、参考帧、POC、缩放矩阵、加权预测、
多切片和硬件参数提交；本桥接只负责设备生命周期、输入/输出、硬件格式约束和像素传输，
不自行实现码流解析或托管的 libva 参数结构。

## 范围与依赖

- Linux 上需要 libavcodec ≥59、libavutil ≥57（FFmpeg 5.1+）、libva、对应 GPU 的 VA 驱动、
  可访问的 DRM render node，以及针对目标系统构建的桥接库。不是系统内置或纯托管实现，
  不需要 GStreamer，也不启动 FFmpeg 命令行进程解码。
- 托管 Media 项目零 NuGet 依赖、NativeAOT 兼容；原生 FFmpeg ABI 由编译器和链接器匹配，
  不在 C# 中复制 AVCodecContext/AVFrame/libva codec 结构的布局。
- 支持驱动实际提供的 **8-bit 4:2:0 H.264、HEVC Main、AV1 Profile 0**。拒绝 10-bit、
  4:2:2/4:4:4 等不符合当前 NV12 输出接口的格式，不宣称 HDR/P010 支持。
- `get_format` 仅返回 `AV_PIX_FMT_VAAPI`，接收帧再次验证 VAAPI 格式与硬件帧上下文。
  显式 VA 选择不回退软件。实际流 profile/尺寸/参考帧需求仍可能在首帧失败。
- Auto 在 **初始化** 时依次尝试 VA → GStreamer 硬件 → GStreamer 软件。
  已开始处理码流后不静默换后端，错误交给调用者重新选择／连接。
- 省略 `VideoDecoderOptions.VaApiRenderNode` 时遍历 `/dev/dri/renderD*`；指定路径时只用该设备。
  capability probe 不缓存，设备权限或可见性改变后可以重试。
- 参数配置包可以单独传入；图片输入须为完整 access unit。显示顺序和 PTS 由 libavcodec
  保留；未知 PTS 为 `long.MinValue`。每次访问单元最多 32 MiB，像素尺寸上限与
  `VideoFrameLimits` 相同。Drain 完成后须 Reset；Reset 清空待输出帧并保留原生参数集。
- 通过 `av_hwframe_transfer_data` 完成驱动同步与平面读回，再对下载帧精确裁剪并复制到
  池化 NV12。不会把 tiled/compressed DMA-BUF 当线性像素。颜色来自输出 metadata，
  用户显式覆盖优先；回调返回后帧释放。不属于零拷贝呈现。
- `VaApiSession` 和 `EglDmaBufImage` 是独立原语，不承担 codec 参数序列化。

## 构建与部署

Debian/Ubuntu 示例（其他发行版使用相应开发包；glibc/musl、x64/arm64 需分别构建）：

```sh
sudo apt-get install cmake pkg-config gcc libavcodec-dev libavutil-dev libva-dev
cmake -S UotanStudio.Media/Native/Linux -B /tmp/uotan-va -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/uotan-va --parallel
ctest --test-dir /tmp/uotan-va --output-on-failure
dotnet publish UotanStudio.Desktop -c Release -r linux-x64 \
  -p:UotanVaNativeLibrary=/tmp/uotan-va/libuotan_media_va.so
```

`UotanVaNativeLibrary` 指定的桥接库随项目引用传递到 Desktop/测试输出与发布目录。
不指定时从系统动态库搜索路径加载，例如由发行版包安装到正常库目录。
桥接库动态链接 FFmpeg，部署系统必须提供构建时对应 soname 的运行库；用 `ldd`
核验依赖，不把不同发行版或架构的二进制混用。桥接不依赖 libswscale。

FFmpeg 默认许可证为 LGPL 2.1+，启用 GPL 等组件会改变其许可条件。桥接不复制 FFmpeg
实现代码；发行版打包、重分发运行库时须保留并遵守所选 FFmpeg 构建的许可和来源要求。
这里没有把某个发行版的 GPL 构建宣称为 LGPL-only，也没有随源码提交 FFmpeg 二进制。

## 验证

CPU-only CTest 测试实际桥接代码的 ABI、禁止软件格式、10-bit 拒绝、带 stride 的裁剪/
复制、奇数可见尺寸、缓冲区边界与错误返回。它不证明 VA 硬件解码。

无需构建 Avalonia 应用的托管侧契约测试位于 `UotanStudio.Tests`，按命名空间定向运行：

```sh
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj \
  --filter "FullyQualifiedName~UotanStudio.Tests.Media.VaApiDecoderTests" \
  --minimum-expected-tests 1
```

这些测试用 fake session 覆盖托管侧调用序列、句柄释放顺序与错误返回，不驱动 `/dev/dri`
渲染节点，因此不证明 VA 硬件解码。

硬件验收需在具备 H.264/HEVC 硬件、已安装 `libuotan_media_va.so` 与 `ffmpeg`/`ffprobe`
（参考解码与夹具生成仅供测试使用）的主机上实测，并把渲染节点显式指向目标卡。缺少硬件、
桥接或驱动时按失败处理，不能用跳过凑通过；硬件输出需与软件参考逐帧比较，而不是只看是否
出帧。未在该类主机上实测前，任何「硬件解码通过」的结论都不成立。

本仓库当前的自动化测试只覆盖托管侧契约与 CPU-only 原生桥接口；真实 VA 硬件解码路径没有
可执行的自动化验收用例，需在具备渲染节点的主机上按上述边界单独实测。

截至 2026-09-10，本机 Debian WSL 无 `/dev/dri`，Linux 桥接构建与 CPU-only 原生测试
已覆盖，**真实 VA GPU 验收需要在具备渲染节点的主机执行**。不得把代码路径完整或
软件参考解码通过写成硬件验证通过。

验证记录（本机 2026-09-10）：

| 检查 | 结果及边界 |
|---|---|
| Windows Media 命名空间回归 | 89 项，75 通过、14 平台/工具条件跳过、0 失败；包含 H.264/HEVC/AV1 的 D3D11 输出测试 |
| Debian 独立 MediaValidation | 28 项，22 通过、6 VA 硬件条件跳过、0 失败；包含 4 个真实软件参考工作流测试 |
| Linux CTest | 原生 ABI、裁剪/平面复制、奇数尺寸、边界、真实软件帧拒绝等断言通过；UBSan 下也通过 |
| Linux NativeAOT | 最小调用宿主发布成功，实际加载桥接并验证缺失设备明确失败；不代表 GPU 解码已执行 |
| 强制硬件验收模式 | 本机 3 个固定夹具项均因无 VA 设备失败、0 跳过；证明验收门不会把环境缺失记成成功 |
| Windows 测试项目构建 | 成功；ContainerStructureTests 的 5 条 xUnit 分析警告与本后端无关 |

Windows 厂商异步 MFT 的 Poll 路径没有异步硬件运行证据；本机 GPU 测试使用可用的
D3D11 后端。macOS 没有硬件运行环境，本文件不为该平台声明可用性。

## 上游依据

- [FFmpeg hw_decode.c](https://github.com/FFmpeg/FFmpeg/blob/n7.1/doc/examples/hw_decode.c)：
  `avcodec_get_hw_config`、硬件 get_format、硬件设备引用、send/receive、帧传输。
- [FFmpeg hwcontext_vaapi.c](https://github.com/FFmpeg/FFmpeg/blob/n7.1/libavutil/hwcontext_vaapi.c)：
  VA 同步、映射与图像传输的驱动适配。
- [libva 2.22 va.h](https://github.com/intel/libva/blob/2.22.0/va/va.h)：
  VLD=1；H.264 profiles=5/6/7/13、HEVC Main=17、AV1 Profile0=32。
