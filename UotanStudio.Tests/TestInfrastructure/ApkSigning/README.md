# APK 签名独立验证夹具

`unsigned.apk` 是本项目生成的 603 字节无代码测试 APK，包名为 `com.uotanstudio.signing.validation`，minSdk=24、targetSdk=28、versionCode=1，`application android:hasCode=false`。仅含二进制 `AndroidManifest.xml` 与 `assets/proof.txt`，没有权限、业务代码、证书或私钥。它已被 Android apksig 解析，并在 MuMu Android 15 上安装。

SHA-256：`AE120B95157566008EBAEF1D3583F9ECFEBB59EF80C7C63E31F02768C99DF439`。

`Verify.java` 调用 Google 官方 `com.android.apksig.ApkVerifier`，不使用项目自己的验签代码；其 `sign` 模式用官方 `ApkSigner` 生成重签名输入。测试覆盖 RSA-2048/4096、EC P-256/P-384、v1/v2/v3，以及上游签名 → 本项目重签名 → 上游验签和篡改拒绝。

运行前设置 `UOTAN_APKSIG_JAR` 为现有 apksig jar 的绝对路径，`UOTAN_JAVA` 为 JDK 17+ 的 java 可执行文件绝对路径。测试本身不下载、不安装软件，不访问设备。缺少工具时明确跳过；工具就绪后的错误或超时使测试失败。每个用例临时生成密钥，并在结束时清理。

本次验证使用 Google Maven 发布的 `com.android.tools.build:apksig:8.9.2`：

- [二进制](https://dl.google.com/dl/android/maven2/com/android/tools/build/apksig/8.9.2/apksig-8.9.2.jar)，SHA-256 `C070ED1394629D74641AA0906F60B2FFA1EE77E6366A1F93437F59717B1AEB89`。
- [源码](https://dl.google.com/dl/android/maven2/com/android/tools/build/apksig/8.9.2/apksig-8.9.2-sources.jar)，SHA-256 `C7A0F43D1E40FDF39725AC090F5C7BBAEAE82D1E0BFD9CF2EE507A70EB5136B5`。

设备安装验证不替代上游 apksig 的签名完整性验证。
