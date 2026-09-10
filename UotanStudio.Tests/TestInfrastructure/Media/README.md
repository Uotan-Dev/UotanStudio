# 固定媒体测试夹具

`black-64x64.h264` 是本项目为原生解码边界验证构造的原创 H.264 Baseline / Level 1.0 测试访问单元：AUD + SPS + PPS + 单 IDR slice，64×64，4×4 个 I_PCM 宏块，全部 Y=16、Cb=Cr=128，4:2:0 8-bit。使用 ITU-T H.264 的 SPS/PPS/slice header / I_PCM 语法和 Annex-B start code、emulation-prevention 规则构造，不依赖被测解码器生成。单帧大小 6209 字节，原生输出应为精确黑色，不允许近似像素通过。

SHA-256：`5BD0CA16A908E858640C20D733E50CB35597D49E34D953A3A40B2FA99F02766E`。

`openh264-qcif-cabac.h264` 原样取自 Cisco OpenH264 v2.6.0 的 `res/test_qcif_cabac.264`，41051 字节，30 个 176×144 图像，单 slice/图像，Main profile CABAC。它与原创 I_PCM 夹具共同验证项目外部码流及普通连续预测帧，不是项目编码器自回环。测试固定校验 SHA-256 并在 Drain 后要求全部 30 帧，以及至少 10 种不同的可见亮度图像。

来源：https://github.com/cisco/openh264/blob/v2.6.0/res/test_qcif_cabac.264

SHA-256：`D5A2D70C45100CF8143572D9E9E48A867C04890690E4DE7633ED4E655DD95F9E`。

上游许可证保存在 `OPENH264-LICENSE.txt`。测试运行不下载文件、安装 codec 或修改系统配置；夹具缺失和哈希不一致必须失败。Mac 硬件测试只在 Mac 上执行，Windows 测试使用本机 Media Foundation；不把非目标平台跳过记作硬件验证通过。

## 2026-09-10 扩展夹具

以下夹具由 Debian 13 的 FFmpeg 7.1.5 独立生成，不依赖被测库。H.265/AV1 为 128×96、10 fps、1 秒动态 testsrc2，8-bit YUV420；AAC 为 48 kHz 双声道静音、0.3 秒 ADTS。测试剥离 ADTS 后传入原始 AAC 包。编码器版本变化可能产生不同字节，仓库固定文件及哈希为本次验证对象。

```sh
ffmpeg -v error -f lavfi -i anullsrc=r=48000:cl=stereo -t 0.3 -c:a aac -f adts silence-48k-stereo.aac
ffmpeg -v error -f lavfi -i testsrc2=size=128x96:rate=10 -t 1 -c:v libx265 -x265-params log-level=error -f hevc testsrc-128x96.h265
ffmpeg -v error -f lavfi -i testsrc2=size=128x96:rate=10 -t 1 -c:v libaom-av1 -cpu-used 8 -f obu testsrc-128x96.obu
```

| 文件 | 字节数 | SHA-256 |
|---|---:|---|
| silence-48k-stereo.aac | 225 | `2D118EBB8D85EE1BBB7B258C7C1679A1C5631EF415BA84F00C7A70EC09932210` |
| testsrc-128x96.h265 | 9797 | `C70E0C692102CB16CBDB4DDF156AE6EBEC67B5B12E8EDC5DF1BAE20CBCC169C2` |
| testsrc-128x96.obu | 4867 | `4321ADBA660244F5D8E55331BA7253AD6D51CC0C4277DA0060899DED1F953CBD` |

## Linux VA-API 直连解码验证（2026-09-10 修复）

`VaApiVideoDecoder` 通过固定 ABI 的 `libuotan_media_va.so` 调用 libavcodec VA-API 硬件后端。
旧托管 VA codec 结构和不完整的自研 H.264/HEVC 解析器已经替换；不需要 GStreamer，
但需要目标平台桥接库、FFmpeg 运行库和 VA 驱动。安装与完整验证步骤见
[Linux VA 后端说明](../../../UotanStudio.Media/Native/Linux/README.md)。

- `LinuxVaApiDecodeTests` 将每帧 NV12 与软件参考逐像素比较（MSE ≤2），核验显示顺序、
  PTS、Drain、Reset；黑色夹具精确验证 Y=16、UV=128，不再以“只有一个哈希”作为黑色证明。
- 新增独立编码器生成的加权 B 帧、多切片、非块对齐可见尺寸和颜色元数据测试。
- `UOTAN_TEST_VA_REQUIRED=1` 时，缺失 H.264/HEVC VA 硬件、桥接或驱动必须失败。
  普通开发环境缺失硬件时明确跳过；AV1 按驱动能力单独门控。硬件前置检查通过后缺工具或
  解码出错均失败。QEMU/WSL/插件存在本身不证明 VA 视频硬件解码能力。
- CPU-only CTest 验证实际桥接的内存布局、裁剪/复制、缓冲区边界和强制硬件格式。
  托管测试核验 codec 分派、重试、PTS、帧释放、回调重入、错误恢复和初始化回退。
- 本机 Debian WSL 没有 `/dev/dri`；原生构建和 CPU-only 测试通过，真实 VA GPU 验收未执行。