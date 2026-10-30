using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// A self-signed WinUSB driver package materialized on disk: the INF, the catalog, the directory that
/// holds both, and the file names the driver store keeps.
/// <para>已物化到磁盘的自签名 WinUSB 驱动包：INF、编录文件、同时承载两者的目录，以及驱动仓库保留的文件名。</para>
/// </summary>
/// <param name="Directory">The package directory.<para>包目录。</para></param>
/// <param name="InfPath">The absolute INF path.<para>INF 的绝对路径。</para></param>
/// <param name="CatalogPath">The absolute catalog path.<para>编录文件的绝对路径。</para></param>
internal sealed record WinUsbSelfSignedPackage(string Directory, string InfPath, string CatalogPath);

/// <summary>
/// Builds a self-signed WinUSB driver package for exactly one device: it writes a device-specific INF
/// that binds the in-box <c>WinUSB</c> service, creates a self-signed code-signing certificate with a
/// 2048-bit machine-key RSA key, adds that certificate to the local-machine <c>Root</c> and
/// <c>TrustedPublisher</c> stores, assembles an Authenticode catalog over the INF, signs it, and then
/// destroys the private key so no other package can ever be signed with it.
/// <para>为且仅为单个设备构建自签名 WinUSB 驱动包：写入只绑定系统内置 <c>WinUSB</c> 服务的设备专用
/// INF，创建带 2048 位机器密钥的自签代码签名证书，把证书加入本机 <c>Root</c> 与
/// <c>TrustedPublisher</c> 存储，为 INF 组装 Authenticode 编录文件并签名，随后销毁私钥，
/// 使该私钥永远无法再签其他包。</para>
/// </summary>
internal static unsafe class WinUsbSelfSignedDriverPackage
{
    /// <summary>The provider name the generated INF records, which identifies a takeover binding.
    /// <para>生成的 INF 记录的提供者名，用于识别接管绑定。</para></summary>
    internal const string ProviderName = "UotanStudio";

    /// <summary>The subject of the self-signed certificate; removal finds the certificate by this name.
    /// <para>自签证书的主体名；移除时按该名称查找证书。</para></summary>
    internal const string CertificateSubject =
        "CN=UotanStudio WinUSB Takeover, O=UotanStudio, OU=Device Takeover";

    /// <summary>The subject the retired driver-signing implementation used, purged from the trust stores
    /// when a takeover runs.
    /// <para>已退役的驱动签名实现使用的主体名，接管运行时从信任存储中清除。</para></summary>
    internal const string LegacyCertificateSubject = "CN=UotanStudio Driver Signing";

    /// <summary>The friendly name shown for the certificate in the Windows certificate stores.
    /// <para>证书在 Windows 证书存储中显示的友好名。</para></summary>
    internal const string CertificateFriendlyName = "UotanStudio WinUSB Takeover";

    private const string UsbDeviceClassGuid = "{88bae032-5a81-49f0-bc3d-a4ff138216d6}";
    private const int KeySizeBits = 2048;

    private static readonly string[] CertificateStoresToWrite = ["Root", "TrustedPublisher"];

    /// <summary>The subject type GUID of an INF catalog member (<c>DE351A42-8E59-11D0-8C47-00C04FC295EE</c>).
    /// <para>INF 编录条目的主体类型 GUID（<c>DE351A42-8E59-11D0-8C47-00C04FC295EE</c>）。</para></summary>
    private static readonly Guid InfSubjectType = new("de351a42-8e59-11d0-8c47-00c04fc295ee");

    /// <summary>
    /// Materializes and signs the package for one hardware id inside <paramref name="directory"/>. The
    /// private key is destroyed before the certificate reaches the stores, so no later package can be signed
    /// with a certificate that is already trusted.
    /// <para>在 <paramref name="directory"/> 内为一个硬件 ID 物化并签名驱动包。私钥在证书写入存储之前销毁，
    /// 因此已受信任的证书无法再用于签其他包。</para>
    /// </summary>
    /// <param name="directory">The existing package directory.<para>已存在的包目录。</para></param>
    /// <param name="hardwareId">The device hardware id the INF matches.<para>INF 匹配的设备硬件 ID。</para></param>
    /// <param name="instanceId">The device instance id, used to derive the interface GUID.
    /// <para>设备实例 ID，用于推导接口 GUID。</para></param>
    /// <param name="deviceName">The device description the INF publishes, or <see langword="null"/> for the
    /// hardware id.<para>INF 发布的设备描述；为 <see langword="null"/> 时使用硬件 ID。</para></param>
    /// <returns>The materialized package.<para>物化后的驱动包。</para></returns>
    /// <exception cref="InvalidOperationException">A native step failed; the message carries the Win32 error.
    /// <para>某个原生步骤失败；消息携带 Win32 错误。</para></exception>
    internal static WinUsbSelfSignedPackage Create(string directory, string hardwareId, string instanceId,
        string? deviceName = null)
    {
        var suffix = BuildSuffix(hardwareId);
        var infPath = Path.Combine(directory, $"uotanstudio-{suffix}.inf");
        var catalogPath = Path.Combine(directory, $"uotanstudio-{suffix}.cat");
        var infFileName = Path.GetFileName(infPath);
        var catalogFileName = Path.GetFileName(catalogPath);
        File.WriteAllText(infPath, BuildInf(hardwareId, deviceName, infFileName, catalogFileName,
            DeriveInterfaceGuid(instanceId)), new UTF8Encoding(false));

        var containerName = $"UotanStudioWinUsb{suffix}";
        var certificate = CreateSelfSignedCertificate(containerName);
        var signed = false;
        try
        {
            CreateCatalog(catalogPath, hardwareId, infPath, infFileName);
            SignCatalog(certificate, catalogPath);
            signed = true;
        }
        finally
        {
            if (signed)
            {
                DeleteKeyContainer(containerName);
                AddCertificateToStores(certificate);
            }
            else
            {
                DeleteKeyContainer(containerName);
            }

            WinUsbTakeoverNativeMethods.CertFreeCertificateContext(certificate);
        }

        return new WinUsbSelfSignedPackage(directory, infPath, catalogPath);
    }

    /// <summary>
    /// Removes the takeover certificate from the local-machine <c>Root</c> and <c>TrustedPublisher</c>
    /// stores, deleting every certificate that carries the takeover subject name.
    /// <para>从本机 <c>Root</c> 与 <c>TrustedPublisher</c> 存储移除接管证书，删除所有带接管主体名的证书。</para>
    /// </summary>
    /// <returns><see langword="true"/> when at least one store was cleaned or held no such certificate.
    /// <para>至少清理了一个存储，或该存储中没有此类证书时为 <see langword="true"/>。</para></returns>
    internal static bool RemoveCertificateFromStores()
        => RemoveCertificates(WinUsbTakeoverNativeMethods.CertSystemStoreLocalMachine, CertificateSubject);

    /// <summary>
    /// Removes certificates the retired driver-signing implementation left in the machine and current-user
    /// trust stores, so no orphaned request root stays trusted. Failures are ignored: the cleanup must never
    /// keep a takeover from running.
    /// <para>移除已退役的驱动签名实现在本机与当前用户信任存储中留下的证书，避免孤立的请求根继续受信。</para>
    /// </summary>
    internal static void PurgeLegacyCertificates()
    {
        try
        {
            _ = RemoveCertificates(WinUsbTakeoverNativeMethods.CertSystemStoreLocalMachine,
                LegacyCertificateSubject);
            _ = RemoveCertificates(WinUsbTakeoverNativeMethods.CertSystemStoreCurrentUser,
                LegacyCertificateSubject);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>
    /// Deletes every certificate carrying <paramref name="subjectName"/> from the <c>Root</c> and
    /// <c>TrustedPublisher</c> stores of one store location.
    /// <para>从指定位置的 <c>Root</c> 与 <c>TrustedPublisher</c> 存储删除所有带 <paramref name="subjectName"/>
    /// 的证书。</para>
    /// </summary>
    /// <param name="location">The store location.<para>存储位置。</para></param>
    /// <param name="subjectName">The certificate subject to match.<para>要匹配的证书主体名。</para></param>
    /// <returns><see langword="true"/> when every matching certificate was removed and every store was opened.
    /// <para>所有匹配证书都被删除且每个存储都成功打开时为 <see langword="true"/>。</para></returns>
    private static bool RemoveCertificates(uint location, string subjectName)
    {
        var subject = EncodeName(subjectName);
        var cleaned = true;
        foreach (var storeName in CertificateStoresToWrite)
        {
            var store = WinUsbTakeoverNativeMethods.OpenStore(location, storeName);
            if (store == IntPtr.Zero)
            {
                cleaned = false;
                continue;
            }

            try
            {
                var nameBlob = subject;
                while (true)
                {
                    var found = WinUsbTakeoverNativeMethods.CertFindCertificateInStore(store,
                        WinUsbTakeoverNativeMethods.X509AsnEncoding, 0,
                        WinUsbTakeoverNativeMethods.CertFindSubjectName, &nameBlob, IntPtr.Zero);
                    if (found == IntPtr.Zero)
                    {
                        break;
                    }

                    if (!WinUsbTakeoverNativeMethods.CertDeleteCertificateFromStore(found))
                    {
                        cleaned = false;
                        break;
                    }
                }
            }
            finally
            {
                WinUsbTakeoverNativeMethods.CertCloseStore(store, 0);
            }
        }

        if (subject.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(subject.Data);
        }

        return cleaned;
    }

    /// <summary>
    /// Builds the device-specific INF that binds the in-box WinUSB service to one hardware id. It imports
    /// the in-box <c>WINUSB.NT</c>, <c>WINUSB.NT.Services</c> and <c>WINUSB.NT.HW</c> sections instead of
    /// restating the WinUSB service, which keeps the in-box service definition and lets the device start.
    /// <para>构建把系统内置 WinUSB 服务绑定到单个硬件 ID 的设备专用 INF。它导入系统内置的 <c>WINUSB.NT</c>、
    /// <c>WINUSB.NT.Services</c> 与 <c>WINUSB.NT.HW</c> 段而不是重述 WinUSB 服务，从而保留系统既有的服务
    /// 定义并让设备正常启动。</para>
    /// </summary>
    /// <param name="hardwareId">The hardware id to match.<para>要匹配的硬件 ID。</para></param>
    /// <param name="deviceName">The device description to publish, or <see langword="null"/> for the hardware id.
    /// <para>要发布的设备描述；为 <see langword="null"/> 时使用硬件 ID。</para></param>
    /// <param name="infFileName">The INF file name the catalog is declared in it.<para>其中声明的 INF 文件名。</para></param>
    /// <param name="catalogFileName">The catalog file name.<para>编录文件名。</para></param>
    /// <param name="interfaceGuid">The device interface GUID to register.<para>要注册的设备接口 GUID。</para></param>
    /// <returns>The INF text.<para>INF 文本。</para></returns>
    private static string BuildInf(string hardwareId, string? deviceName, string infFileName,
        string catalogFileName, Guid interfaceGuid)
    {
        var version = DateTime.UtcNow.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.AppendLine("; UotanStudio WinUSB takeover package");
        builder.AppendLine("[Version]");
        builder.AppendLine("Signature   = \"$Windows NT$\"");
        builder.AppendLine("Class       = USBDevice");
        builder.AppendLine($"ClassGuid   = {UsbDeviceClassGuid}");
        builder.AppendLine($"Provider    = \"{ProviderName}\"");
        builder.AppendLine($"CatalogFile = {catalogFileName}");
        builder.AppendLine($"DriverVer   = {version},1.0.0.0");
        builder.AppendLine("PnpLockdown = 1");
        builder.AppendLine();
        builder.AppendLine("[Manufacturer]");
        builder.AppendLine("%UotanStudioManufacturer% = UotanStudioWinUsb,NTx86,NTamd64,NTarm64");
        builder.AppendLine();
        foreach (var decoration in (string[])["NTx86", "NTamd64", "NTarm64"])
        {
            builder.AppendLine($"[UotanStudioWinUsb.{decoration}]");
            builder.AppendLine($"%UotanStudioDeviceName% = UotanStudioWinUsb_Install, \"{hardwareId}\"");
            builder.AppendLine();
        }

        builder.AppendLine("[UotanStudioWinUsb_Install]");
        builder.AppendLine("Include = winusb.inf");
        builder.AppendLine("Needs   = WINUSB.NT");
        builder.AppendLine();
        builder.AppendLine("[UotanStudioWinUsb_Install.Services]");
        builder.AppendLine("Include = winusb.inf");
        builder.AppendLine("Needs   = WINUSB.NT.Services");
        builder.AppendLine();
        builder.AppendLine("[UotanStudioWinUsb_Install.HW]");
        builder.AppendLine("Include = winusb.inf");
        builder.AppendLine("Needs   = WINUSB.NT.HW");
        builder.AppendLine("AddReg  = UotanStudioWinUsb_DeviceInterface");
        builder.AppendLine();
        builder.AppendLine("[UotanStudioWinUsb_DeviceInterface]");
        builder.AppendLine("HKR,,DeviceInterfaceGUIDs,0x10000,%UotanStudioDeviceInterfaceGuid%");
        builder.AppendLine();
        builder.AppendLine("[Strings]");
        builder.AppendLine($"UotanStudioManufacturer = \"{ProviderName}\"");
        builder.AppendLine($"UotanStudioDeviceName = \"{BuildDeviceName(deviceName, hardwareId)}\"");
        builder.AppendLine($"UotanStudioDeviceInterfaceGuid = \"{{{interfaceGuid:D}}}\"");
        builder.AppendLine($"; {infFileName}");
        return builder.ToString();
    }

    private static string BuildDeviceName(string? deviceName, string hardwareId)
        => string.IsNullOrWhiteSpace(deviceName)
            ? $"UotanStudio WinUSB ({Sanitize(hardwareId)})"
            : Sanitize(deviceName.Trim());

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is >= ' ' and <= '~' and not '"' and not ',' ? character : '_');
        }

        return builder.ToString();
    }

    private static string BuildSuffix(string hardwareId)
        => Sha256.ComputeHexLower(Encoding.UTF8.GetBytes(hardwareId))[..8];

    private static Guid DeriveInterfaceGuid(string instanceId)
    {
        var digest = Sha256.ComputeHash(Encoding.UTF8.GetBytes($"UotanStudio|{instanceId}"));
        Span<byte> bytes = stackalloc byte[16];
        digest.AsSpan(0, 16).CopyTo(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static WinUsbTakeoverNativeMethods.CryptDataBlob EncodeName(string subject)
    {
        var size = 0u;
        if (!WinUsbTakeoverNativeMethods.CertStrToName(WinUsbTakeoverNativeMethods.X509AsnEncoding, subject,
                WinUsbTakeoverNativeMethods.CertX500NameStr, IntPtr.Zero, null, ref size, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                WinUsbTakeoverNativeMethods.DescribeLastError("Certificate subject encoding failed"));
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        var encoded = (byte*)buffer;
        if (!WinUsbTakeoverNativeMethods.CertStrToName(WinUsbTakeoverNativeMethods.X509AsnEncoding, subject,
                WinUsbTakeoverNativeMethods.CertX500NameStr, IntPtr.Zero, encoded, ref size, IntPtr.Zero))
        {
            Marshal.FreeHGlobal(buffer);
            throw new InvalidOperationException(
                WinUsbTakeoverNativeMethods.DescribeLastError("Certificate subject encoding failed"));
        }

        return new WinUsbTakeoverNativeMethods.CryptDataBlob { Length = size, Data = buffer };
    }

    private static IntPtr CreateSelfSignedCertificate(string containerName)
    {
        var name = EncodeName(CertificateSubject);
        var container = Marshal.StringToHGlobalUni(containerName);
        var signatureObjectId = Marshal.StringToHGlobalAnsi(WinUsbTakeoverNativeMethods.Sha256WithRsaObjId);
        var usageObjectId = Marshal.StringToHGlobalAnsi(WinUsbTakeoverNativeMethods.CodeSigningUsageObjId);
        var extensionObjectId = Marshal.StringToHGlobalAnsi(WinUsbTakeoverNativeMethods.EnhancedKeyUsageObjId);
        IntPtr encodedUsage = IntPtr.Zero;
        IntPtr provider = IntPtr.Zero;
        IntPtr key = IntPtr.Zero;
        try
        {
            if (!AcquireMachineKeyset(container, out provider))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Key container creation failed"));
            }

            if (!WinUsbTakeoverNativeMethods.CryptGenKey(provider, WinUsbTakeoverNativeMethods.AtSignature,
                    ((uint)KeySizeBits << 16) | WinUsbTakeoverNativeMethods.CryptExportable, out key))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Signing key generation failed"));
            }

            var usageArray = stackalloc IntPtr[1];
            usageArray[0] = usageObjectId;
            var enhancedKeyUsage = new WinUsbTakeoverNativeMethods.CertEnhKeyUsage
            {
                Count = 1,
                Usages = (IntPtr)usageArray
            };
            var encodedSize = 0u;
            if (!WinUsbTakeoverNativeMethods.CryptEncodeObject(WinUsbTakeoverNativeMethods.X509AsnEncoding,
                    WinUsbTakeoverNativeMethods.EnhancedKeyUsageObjId, &enhancedKeyUsage, null, ref encodedSize))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Code-signing extension encoding failed"));
            }

            encodedUsage = Marshal.AllocHGlobal((int)encodedSize);
            if (!WinUsbTakeoverNativeMethods.CryptEncodeObject(WinUsbTakeoverNativeMethods.X509AsnEncoding,
                    WinUsbTakeoverNativeMethods.EnhancedKeyUsageObjId, &enhancedKeyUsage, (byte*)encodedUsage,
                    ref encodedSize))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Code-signing extension encoding failed"));
            }

            var extensionArray = stackalloc WinUsbTakeoverNativeMethods.CertExtension[1];
            extensionArray[0] = new WinUsbTakeoverNativeMethods.CertExtension
            {
                ObjectId = extensionObjectId,
                Critical = 1,
                Value = new WinUsbTakeoverNativeMethods.CryptDataBlob
                {
                    Length = encodedSize,
                    Data = encodedUsage
                }
            };
            var extensions = new WinUsbTakeoverNativeMethods.CertExtensions
            {
                Count = 1,
                Extensions = (IntPtr)extensionArray
            };
            var keyProvider = new WinUsbTakeoverNativeMethods.CryptKeyProvInfo
            {
                ContainerName = container,
                ProviderName = IntPtr.Zero,
                ProviderType = WinUsbTakeoverNativeMethods.ProvRsaFull,
                Flags = WinUsbTakeoverNativeMethods.CryptMachineKeyset,
                ParameterCount = 0,
                Parameters = IntPtr.Zero,
                KeySpec = WinUsbTakeoverNativeMethods.AtSignature
            };
            var signatureAlgorithm = new WinUsbTakeoverNativeMethods.CryptAlgorithmIdentifier
            {
                ObjectId = signatureObjectId,
                Parameters = new WinUsbTakeoverNativeMethods.CryptDataBlob()
            };
            var expires = SystemTime.FromDateTime(DateTime.UtcNow.AddYears(5));
            var subject = name;
            var certificate = WinUsbTakeoverNativeMethods.CertCreateSelfSignCertificate(IntPtr.Zero, &subject, 0,
                &keyProvider, &signatureAlgorithm, null, &expires, &extensions);
            if (certificate == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Self-signed certificate creation failed"));
            }

            return certificate;
        }
        finally
        {
            if (key != IntPtr.Zero)
            {
                WinUsbTakeoverNativeMethods.CryptDestroyKey(key);
            }

            if (provider != IntPtr.Zero)
            {
                WinUsbTakeoverNativeMethods.CryptReleaseContext(provider, 0);
            }

            Marshal.FreeHGlobal(name.Data);
            Marshal.FreeHGlobal(container);
            Marshal.FreeHGlobal(signatureObjectId);
            Marshal.FreeHGlobal(usageObjectId);
            Marshal.FreeHGlobal(extensionObjectId);
            if (encodedUsage != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(encodedUsage);
            }
        }
    }

    private static void CreateCatalog(string catalogPath, string hardwareId, string infPath, string infFileName)
    {
        var provider = IntPtr.Zero;
        var catalogPathPointer = Marshal.StringToHGlobalUni(catalogPath);
        var hardwareIdPointer = Marshal.StringToHGlobalUni(hardwareId.ToLowerInvariant());
        var operatingSystemPointer = Marshal.StringToHGlobalUni(WinUsbTakeoverNativeMethods
            .CatOperatingSystemAttribute);
        var hardwareIdTag = Marshal.StringToHGlobalUni("HWID1");
        var operatingSystemTag = Marshal.StringToHGlobalUni("OS");
        var fileNameTag = Marshal.StringToHGlobalUni("File");
        var operatingSystemAttributeTag = Marshal.StringToHGlobalUni("OSAttr");
        var signatureObjectId = Marshal.StringToHGlobalAnsi(WinUsbTakeoverNativeMethods.SpcCabDataObjId);
        var digestObjectId = Marshal.StringToHGlobalAnsi(WinUsbTakeoverNativeMethods.Sha1ObjId);
        var obsoleteLink = Marshal.StringToHGlobalUni("<<<Obsolete>>>");
        IntPtr catalog = IntPtr.Zero;
        IntPtr encodedLink = IntPtr.Zero;
        try
        {
            if (!WinUsbTakeoverNativeMethods.CryptAcquireContext(out provider, IntPtr.Zero, IntPtr.Zero,
                    WinUsbTakeoverNativeMethods.ProvRsaFull, WinUsbTakeoverNativeMethods.CryptVerifyContext))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Catalog provider acquisition failed"));
            }

            catalog = WinUsbTakeoverNativeMethods.CryptCatOpen(catalogPathPointer,
                WinUsbTakeoverNativeMethods.CryptCatOpenCreateNew, provider, 0, 0);
            if (catalog == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Catalog creation failed"));
            }

            var attributeFlags = WinUsbTakeoverNativeMethods.CryptCatAttrAuthenticated |
                WinUsbTakeoverNativeMethods.CryptCatAttrNameAscii |
                WinUsbTakeoverNativeMethods.CryptCatAttrDataAscii;
            var hardwareIdBytes = (uint)((hardwareId.Length + 1) * 2);
            if (WinUsbTakeoverNativeMethods.CryptCatPutCatAttribute(catalog, hardwareIdTag, attributeFlags,
                    hardwareIdBytes, (byte*)hardwareIdPointer) == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Catalog HWID attribute creation failed"));
            }

            var operatingSystemBytes = (uint)((WinUsbTakeoverNativeMethods.CatOperatingSystemAttribute.Length + 1)
                * 2);
            if (WinUsbTakeoverNativeMethods.CryptCatPutCatAttribute(catalog, operatingSystemTag, attributeFlags,
                    operatingSystemBytes, (byte*)operatingSystemPointer) == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Catalog OS attribute creation failed"));
            }

            var hash = stackalloc byte[WinUsbTakeoverNativeMethods.Sha1HashLength];
            var hashSize = (uint)WinUsbTakeoverNativeMethods.Sha1HashLength;
            using (var stream = File.OpenRead(infPath))
            {
                if (!WinUsbTakeoverNativeMethods.CryptCATAdminCalcHashFromFileHandle(
                        stream.SafeFileHandle.DangerousGetHandle(), ref hashSize, hash, 0))
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError("Catalog hash computation failed"));
                }
            }

            var tag = Marshal.StringToHGlobalUni(WinUsbTakeoverNativeMethods.ToReferenceTag(
                new ReadOnlySpan<byte>(hash, (int)hashSize)));
            var lowerName = Marshal.StringToHGlobalUni(infFileName.ToLowerInvariant());
            try
            {
                var link = new WinUsbTakeoverNativeMethods.SpcLink
                {
                    LinkChoice = WinUsbTakeoverNativeMethods.SpcFileLinkChoice,
                    Url = obsoleteLink
                };
                var encodedSize = 0u;
                if (!WinUsbTakeoverNativeMethods.CryptEncodeObject(WinUsbTakeoverNativeMethods.X509AsnEncoding,
                        WinUsbTakeoverNativeMethods.SpcCabDataObjId, &link, null, ref encodedSize))
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError("Catalog link encoding failed"));
                }

                encodedLink = Marshal.AllocHGlobal((int)encodedSize);
                if (!WinUsbTakeoverNativeMethods.CryptEncodeObject(WinUsbTakeoverNativeMethods.X509AsnEncoding,
                        WinUsbTakeoverNativeMethods.SpcCabDataObjId, &link, (byte*)encodedLink, ref encodedSize))
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError("Catalog link encoding failed"));
                }

                var indirectData = new WinUsbTakeoverNativeMethods.SpcIndirectData
                {
                    Data = new WinUsbTakeoverNativeMethods.CryptAttributeTypeValue
                    {
                        ObjectId = signatureObjectId,
                        Value = new WinUsbTakeoverNativeMethods.CryptDataBlob
                        {
                            Length = encodedSize,
                            Data = encodedLink
                        }
                    },
                    DigestAlgorithm = new WinUsbTakeoverNativeMethods.CryptAlgorithmIdentifier
                    {
                        ObjectId = digestObjectId,
                        Parameters = new WinUsbTakeoverNativeMethods.CryptDataBlob()
                    },
                    Digest = new WinUsbTakeoverNativeMethods.CryptDataBlob
                    {
                        Length = hashSize,
                        Data = (IntPtr)hash
                    }
                };
                var subjectType = InfSubjectType;
                var member = WinUsbTakeoverNativeMethods.CryptCatPutMember(catalog, IntPtr.Zero, tag, &subjectType,
                    WinUsbTakeoverNativeMethods.CryptCatMemberVersion,
                    (uint)sizeof(WinUsbTakeoverNativeMethods.SpcIndirectData), (byte*)&indirectData);
                if (member == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError("Catalog member creation failed"));
                }

                var nameBytes = (uint)((infFileName.Length + 1) * 2);
                if (WinUsbTakeoverNativeMethods.CryptCatPutMemberAttribute(catalog, member, fileNameTag,
                        attributeFlags, nameBytes, (byte*)lowerName) == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError("Catalog file attribute creation failed"));
                }

                var operatingSystemAttributeBytes =
                    (uint)((WinUsbTakeoverNativeMethods.CatOperatingSystemAttribute.Length + 1) * 2);
                if (WinUsbTakeoverNativeMethods.CryptCatPutMemberAttribute(catalog, member,
                        operatingSystemAttributeTag, attributeFlags, operatingSystemAttributeBytes,
                        (byte*)operatingSystemPointer) == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError("Catalog OS attribute creation failed"));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tag);
                Marshal.FreeHGlobal(lowerName);
            }

            if (!WinUsbTakeoverNativeMethods.CryptCatPersistStore(catalog))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Catalog persist failed"));
            }
        }
        finally
        {
            if (catalog != IntPtr.Zero)
            {
                WinUsbTakeoverNativeMethods.CryptCatClose(catalog);
            }

            if (provider != IntPtr.Zero)
            {
                WinUsbTakeoverNativeMethods.CryptReleaseContext(provider, 0);
            }

            if (encodedLink != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(encodedLink);
            }

            Marshal.FreeHGlobal(catalogPathPointer);
            Marshal.FreeHGlobal(hardwareIdPointer);
            Marshal.FreeHGlobal(operatingSystemPointer);
            Marshal.FreeHGlobal(hardwareIdTag);
            Marshal.FreeHGlobal(operatingSystemTag);
            Marshal.FreeHGlobal(fileNameTag);
            Marshal.FreeHGlobal(operatingSystemAttributeTag);
            Marshal.FreeHGlobal(signatureObjectId);
            Marshal.FreeHGlobal(digestObjectId);
            Marshal.FreeHGlobal(obsoleteLink);
        }
    }

    private static void SignCatalog(IntPtr certificate, string catalogPath)
    {
        var fileName = Marshal.StringToHGlobalUni(catalogPath);
        var index = 0u;
        try
        {
            var fileInfo = new WinUsbTakeoverNativeMethods.SignerFileInfo
            {
                Size = (uint)sizeof(WinUsbTakeoverNativeMethods.SignerFileInfo),
                FileName = fileName,
                File = IntPtr.Zero
            };
            var subject = new WinUsbTakeoverNativeMethods.SignerSubjectInfo
            {
                Size = (uint)sizeof(WinUsbTakeoverNativeMethods.SignerSubjectInfo),
                Index = (IntPtr)(&index),
                SubjectChoice = WinUsbTakeoverNativeMethods.SignerSubjectFile,
                Subject = (IntPtr)(&fileInfo)
            };
            var storeInfo = new WinUsbTakeoverNativeMethods.SignerCertStoreInfo
            {
                Size = (uint)sizeof(WinUsbTakeoverNativeMethods.SignerCertStoreInfo),
                SigningCert = certificate,
                CertPolicy = WinUsbTakeoverNativeMethods.SignerCertPolicyChain,
                CertStore = IntPtr.Zero
            };
            var signerCertificate = new WinUsbTakeoverNativeMethods.SignerCert
            {
                Size = (uint)sizeof(WinUsbTakeoverNativeMethods.SignerCert),
                CertChoice = WinUsbTakeoverNativeMethods.SignerCertStore,
                CertInfo = (IntPtr)(&storeInfo),
                Window = IntPtr.Zero
            };
            var signature = new WinUsbTakeoverNativeMethods.SignerSignatureInfo
            {
                Size = (uint)sizeof(WinUsbTakeoverNativeMethods.SignerSignatureInfo),
                HashAlgorithm = WinUsbTakeoverNativeMethods.CalgSha256,
                AttributeChoice = WinUsbTakeoverNativeMethods.SignerNoAttr,
                Attribute = IntPtr.Zero,
                Authenticated = IntPtr.Zero,
                Unauthenticated = IntPtr.Zero
            };
            var result = WinUsbTakeoverNativeMethods.SignerSignEx(0, &subject, &signerCertificate, &signature,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var context);
            if (context != IntPtr.Zero)
            {
                WinUsbTakeoverNativeMethods.SignerFreeSignerContext(context);
            }

            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"Catalog signing failed with HRESULT 0x{result:X8}. "
                    + $"编录文件签名失败，HRESULT 为 0x{result:X8}。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileName);
        }
    }

    /// <summary>
    /// Acquires the machine key container for one takeover package, creating it. A container left behind by
    /// an interrupted takeover is deleted first, so a repeated takeover of the same hardware id never fails
    /// on an existing container.
    /// <para>获取一个接管包所用的机器密钥容器，并在需要时创建它。被中断的接管残留的容器会先被删除，因此同一
    /// 硬件 ID 的重复接管不会因容器已存在而失败。</para>
    /// </summary>
    /// <param name="container">The unmanaged container name.<para>非托管容器名。</para></param>
    /// <param name="provider">The acquired provider handle.<para>获取到的提供者句柄。</para></param>
    /// <returns><see langword="true"/> when the container is available.
    /// <para>容器可用时为 <see langword="true"/>。</para></returns>
    private static bool AcquireMachineKeyset(IntPtr container, out IntPtr provider)
    {
        var createFlags = WinUsbTakeoverNativeMethods.CryptMachineKeyset |
            WinUsbTakeoverNativeMethods.CryptNewKeyset |
            WinUsbTakeoverNativeMethods.CryptSilent;
        if (WinUsbTakeoverNativeMethods.CryptAcquireContext(out provider, container, IntPtr.Zero,
                WinUsbTakeoverNativeMethods.ProvRsaFull, createFlags))
        {
            return true;
        }

        DeleteKeyContainer(container);
        return WinUsbTakeoverNativeMethods.CryptAcquireContext(out provider, container, IntPtr.Zero,
            WinUsbTakeoverNativeMethods.ProvRsaFull, createFlags);
    }

    /// <summary>
    /// Deletes the machine key container behind a takeover package, leaving every other container alone.
    /// <para>删除一个接管包所用的机器密钥容器，不影响其他容器。</para>
    /// </summary>
    /// <param name="containerName">The container name.<para>容器名。</para></param>
    private static void DeleteKeyContainer(string containerName)
    {
        var container = Marshal.StringToHGlobalUni(containerName);
        try
        {
            DeleteKeyContainer(container);
        }
        finally
        {
            Marshal.FreeHGlobal(container);
        }
    }

    /// <summary>
    /// Deletes one machine key container addressed by its unmanaged name.
    /// <para>按非托管容器名删除一个机器密钥容器。</para>
    /// </summary>
    /// <param name="container">The unmanaged container name.<para>非托管容器名。</para></param>
    private static void DeleteKeyContainer(IntPtr container)
    {
        if (WinUsbTakeoverNativeMethods.CryptAcquireContext(out var provider, container, IntPtr.Zero,
                WinUsbTakeoverNativeMethods.ProvRsaFull,
                WinUsbTakeoverNativeMethods.CryptMachineKeyset | WinUsbTakeoverNativeMethods.CryptSilent |
                WinUsbTakeoverNativeMethods.CryptDeleteKeyset) && provider != IntPtr.Zero)
        {
            WinUsbTakeoverNativeMethods.CryptReleaseContext(provider, 0);
        }
    }

    private static void AddCertificateToStores(IntPtr certificate)
    {
        var context = *(CertificateContext*)certificate;
        if (context.Encoded == IntPtr.Zero || context.EncodedSize == 0)
        {
            throw new InvalidOperationException("The generated certificate carries no encoded payload.");
        }

        var friendlyName = Marshal.StringToHGlobalUni(CertificateFriendlyName);
        try
        {
            var blob = new WinUsbTakeoverNativeMethods.CryptDataBlob
            {
                Length = (uint)(CertificateFriendlyName.Length * 2),
                Data = friendlyName
            };
            if (!WinUsbTakeoverNativeMethods.CertSetCertificateContextProperty(certificate,
                    WinUsbTakeoverNativeMethods.CertFriendlyNamePropId, 0, &blob))
            {
                throw new InvalidOperationException(
                    WinUsbTakeoverNativeMethods.DescribeLastError("Certificate friendly name failed"));
            }

            foreach (var storeName in CertificateStoresToWrite)
            {
                var store = WinUsbTakeoverNativeMethods.OpenStore(
                    WinUsbTakeoverNativeMethods.CertSystemStoreLocalMachine, storeName);
                if (store == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        WinUsbTakeoverNativeMethods.DescribeLastError($"Opening store '{storeName}' failed"));
                }

                try
                {
                    if (!WinUsbTakeoverNativeMethods.CertAddEncodedCertificateToStore(store,
                            WinUsbTakeoverNativeMethods.X509AsnEncoding, (byte*)context.Encoded,
                            context.EncodedSize, WinUsbTakeoverNativeMethods.CertStoreAddReplaceExisting,
                            out var stored))
                    {
                        throw new InvalidOperationException(
                            WinUsbTakeoverNativeMethods.DescribeLastError(
                                $"Adding the certificate to '{storeName}' failed"));
                    }

                    if (stored != IntPtr.Zero)
                    {
                        WinUsbTakeoverNativeMethods.CertFreeCertificateContext(stored);
                    }
                }
                finally
                {
                    WinUsbTakeoverNativeMethods.CertCloseStore(store, 0);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(friendlyName);
        }
    }

    /// <summary>The leading fields of <c>CERT_CONTEXT</c>: encoding, encoded bytes, and the certificate info.
    /// <para><c>CERT_CONTEXT</c> 的前导字段：编码类型、编码字节与证书信息。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint Encoding;
        internal IntPtr Encoded;
        internal uint EncodedSize;
    }

    /// <summary>The <c>SYSTEMTIME</c> shape used for the certificate validity window.
    /// <para>用于证书有效期窗口的 <c>SYSTEMTIME</c> 形状。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        internal ushort Year;
        internal ushort Month;
        internal ushort DayOfWeek;
        internal ushort Day;
        internal ushort Hour;
        internal ushort Minute;
        internal ushort Second;
        internal ushort Milliseconds;

        internal static SystemTime FromDateTime(DateTime value) => new()
        {
            Year = (ushort)value.Year,
            Month = (ushort)value.Month,
            DayOfWeek = (ushort)value.DayOfWeek,
            Day = (ushort)value.Day,
            Hour = (ushort)value.Hour,
            Minute = (ushort)value.Minute,
            Second = (ushort)value.Second,
            Milliseconds = (ushort)value.Millisecond
        };
    }
}