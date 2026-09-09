# 固定媒体测试夹具

`black-64x64.h264` 是本项目为原生解码边界验证构造的原创 H.264 Baseline / Level 1.0 测试访问单元：AUD + SPS + PPS + 单 IDR slice，64×64，4×4 个 I_PCM 宏块，全部 Y=16、Cb=Cr=128，4:2:0 8-bit。使用 ITU-T H.264 的 SPS/PPS/slice header / I_PCM 语法和 Annex-B start code、emulation-prevention 规则构造，不依赖被测解码器生成。单帧大小 6209 字节，原生输出应为精确黑色，不允许近似像素通过。

SHA-256：`5BD0CA16A908E858640C20D733E50CB35597D49E34D953A3A40B2FA99F02766E`。

`openh264-qcif-cabac.h264` 原样取自 Cisco OpenH264 v2.6.0 的 `res/test_qcif_cabac.264`，41051 字节，30 个 176×144 图像，单 slice/图像，Main profile CABAC。它与原创 I_PCM 夹具共同验证项目外部码流及普通连续预测帧，不是项目编码器自回环。测试固定校验 SHA-256 并在 Drain 后要求全部 30 帧，以及至少 10 种不同的可见亮度图像。

来源：https://github.com/cisco/openh264/blob/v2.6.0/res/test_qcif_cabac.264

SHA-256：`D5A2D70C45100CF8143572D9E9E48A867C04890690E4DE7633ED4E655DD95F9E`。

上游许可证保存在 `OPENH264-LICENSE.txt`。测试运行不下载文件、安装 codec 或修改系统配置；夹具缺失和哈希不一致必须失败。Mac 硬件测试只在 Mac 上执行，Windows 测试使用本机 Media Foundation；不把非目标平台跳过记作硬件验证通过。
