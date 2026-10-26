using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// Creates the WinUSB driver catalog and signs it. The catalog is produced through the documented
/// driver catalog administration sequence: acquire a <c>DRIVER_ACTION_VERIFY</c> context, hash the
/// INF with <c>CryptCATAdminCalcHashFromFileHandle</c>, register the package with
/// <c>CryptCATAdminAddCatalog</c>, take the generated catalog path from
/// <c>CryptCATCatalogInfoFromContext</c>, then Authenticode-sign that catalog with
/// <c>SignerSignEx</c> from mssign32 using SHA-256 and the SPC opus/statement attributes, mirroring
/// the libwdi <c>SelfSignFile</c> layout.
/// <para>创建并签名 WinUSB 驱动目录。目录按文档化的驱动目录管理序列生成:获取
/// <c>DRIVER_ACTION_VERIFY</c> 上下文、用 <c>CryptCATAdminCalcHashFromFileHandle</c> 计算 INF
/// 哈希、经 <c>CryptCATAdminAddCatalog</c> 注册驱动包、用 <c>CryptCATCatalogInfoFromContext</c>
/// 取得生成的目录路径,再按 libwdi <c>SelfSignFile</c> 的结构布局,以 mssign32 的
/// <c>SignerSignEx</c>、SHA-256 与 SPC opus/statement 属性做 Authenticode 签名。</para>
/// </summary>
internal static class WinUsbDriverCatalog
{
    private const uint SignerSubjectFile = 1;
    private const uint SignerCertStore = 2;
    private const uint SignerCertPolicyChain = 2;
    private const uint SignerNoAttr = 0;

    private const string SpOpusInfoOid = "1.3.6.1.4.1.311.2.1.12";
    private const string StatementTypeOid = "1.3.6.1.4.1.311.2.1.11";

    private static readonly byte[] SpOpusInfoData = [0x30, 0x00];
    private static readonly byte[] StatementTypeData =
        [0x30, 0x0c, 0x06, 0x0a, 0x2b, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x01, 0x15];

    /// <summary>
    /// Registers the driver package in the catalog database and signs the generated catalog with
    /// the given certificate.
    /// <para>把驱动包注册进目录数据库,并用给定证书签名生成的目录。</para>
    /// </summary>
    /// <param name="infPath">The full path of the INF to hash and register.
    /// <para>要计算哈希并注册的 INF 完整路径。</para></param>
    /// <param name="certificate">The code-signing certificate with a private key.
    /// <para>带私钥的代码签名证书。</para></param>
    /// <param name="catalogPath">
    /// The full path of the signed catalog inside the catalog database on success.
    /// <para>成功时目录数据库内已签名目录的完整路径。</para>
    /// </param>
    /// <param name="error">The failure reason when this returns <see langword="false"/>.
    /// <para>返回 <see langword="false"/> 时的失败原因。</para></param>
    /// <param name="verificationNote">
    /// A non-blocking note appended when the optional <c>WinVerifyTrust</c> check of the signed
    /// catalog does not succeed.
    /// <para>对已签名目录的可选 <c>WinVerifyTrust</c> 校验未通过时附加的非阻断说明。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the catalog was created and signed; catalog creation and
    /// signing failures are fatal, verification is not.
    /// <para>目录已创建并签名时返回 <see langword="true"/>;目录创建与签名失败是致命的,验签不是。</para>
    /// </returns>
    internal static bool TryCreateAndSign(string infPath, X509Certificate2 certificate,
        [NotNullWhen(true)] out string? catalogPath, [NotNullWhen(false)] out string? error, out string? verificationNote)
    {
        catalogPath = null;
        verificationNote = null;

        if (!WinUsbDriverNativeMethods.CryptCATAdminAcquireContext(out var catAdmin,
                WinUsbDriverNativeMethods.DriverActionVerify, 0))
        {
            error = WinUsbDriverNativeMethods.DescribeLastWin32Error("CryptCATAdminAcquireContext failed");
            return false;
        }

        var catInfo = IntPtr.Zero;
        try
        {
            if (!TryHashInf(infPath, out var hashError))
            {
                error = hashError ?? "INF hashing failed without a reason.";
                return false;
            }

            catInfo = WinUsbDriverNativeMethods.CryptCATAdminAddCatalog(catAdmin, infPath, null, 0);
            if (catInfo == IntPtr.Zero)
            {
                error = WinUsbDriverNativeMethods.DescribeLastWin32Error("CryptCATAdminAddCatalog failed");
                return false;
            }

            var info = new WinUsbDriverNativeMethods.CatalogInfo
            {
                CbSize = (uint)Marshal.SizeOf<WinUsbDriverNativeMethods.CatalogInfo>()
            };
            if (!WinUsbDriverNativeMethods.CryptCATCatalogInfoFromContext(catInfo, ref info, 0))
            {
                error = WinUsbDriverNativeMethods.DescribeLastWin32Error("CryptCATCatalogInfoFromContext failed");
                return false;
            }

            catalogPath = WinUsbDriverNativeMethods.ReadCatalogFile(ref info);
            var signError = TrySignFile(catalogPath, certificate);
            if (signError is not null)
            {
                error = signError;
                return false;
            }

            verificationNote = TryVerifyCatalog(catalogPath);
            error = null;
            return true;
        }
        finally
        {
            if (catInfo != IntPtr.Zero)
            {
                _ = WinUsbDriverNativeMethods.CryptCATAdminReleaseCatalogContext(catAdmin, catInfo, 0);
            }

            _ = WinUsbDriverNativeMethods.CryptCATAdminReleaseContext(catAdmin, 0);
        }
    }

    private static bool TryHashInf(string infPath, out string? error)
    {
        using var stream = new FileStream(infPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = new byte[64];
        var hashBytes = (uint)hash.Length;
        while (hashBytes <= 1024)
        {
            if (WinUsbDriverNativeMethods.CryptCATAdminCalcHashFromFileHandle(stream.SafeFileHandle, ref hashBytes, hash, 0))
            {
                error = null;
                return true;
            }

            if (hashBytes > hash.Length)
            {
                if (hashBytes > 1024)
                {
                    break;
                }

                hash = new byte[hashBytes];
                continue;
            }

            break;
        }

        error = WinUsbDriverNativeMethods.DescribeLastWin32Error("CryptCATAdminCalcHashFromFileHandle failed");
        return false;
    }

    private static string? TrySignFile(string filePath, X509Certificate2 certificate)
    {
        var heap = new List<IntPtr>();
        try
        {
            var dwIndex = AllocBytes(heap, 4);
            Marshal.WriteInt32(dwIndex, 0);

            var fileName = Marshal.StringToHGlobalUni(filePath);
            heap.Add(fileName);
            var fileInfo = AllocStructure<SignerFileInfo>(heap);
            Marshal.StructureToPtr(new SignerFileInfo
            {
                CbSize = (uint)Marshal.SizeOf<SignerFileInfo>(),
                FileName = fileName,
                File = IntPtr.Zero
            }, fileInfo, false);

            var subjectInfo = AllocStructure<SignerSubjectInfo>(heap);
            Marshal.StructureToPtr(new SignerSubjectInfo
            {
                CbSize = (uint)Marshal.SizeOf<SignerSubjectInfo>(),
                Index = dwIndex,
                SubjectChoice = SignerSubjectFile,
                Subject = fileInfo
            }, subjectInfo, false);

            var certStoreInfo = AllocStructure<SignerCertStoreInfo>(heap);
            Marshal.StructureToPtr(new SignerCertStoreInfo
            {
                CbSize = (uint)Marshal.SizeOf<SignerCertStoreInfo>(),
                SigningCert = certificate.Handle,
                CertPolicy = SignerCertPolicyChain,
                CertStore = IntPtr.Zero
            }, certStoreInfo, false);

            var signerCert = AllocStructure<SignerCert>(heap);
            Marshal.StructureToPtr(new SignerCert
            {
                CbSize = (uint)Marshal.SizeOf<SignerCert>(),
                CertChoice = SignerCertStore,
                StoreInfo = certStoreInfo,
                Hwnd = IntPtr.Zero
            }, signerCert, false);

            var attributes = BuildAuthenticatedAttributes(heap);
            var signatureInfo = AllocStructure<SignerSignatureInfo>(heap);
            Marshal.StructureToPtr(new SignerSignatureInfo
            {
                CbSize = (uint)Marshal.SizeOf<SignerSignatureInfo>(),
                HashAlgorithm = WinUsbDriverNativeMethods.AlgIdSha256,
                AttrChoice = SignerNoAttr,
                AttrAuthcode = IntPtr.Zero,
                AuthenticatedAttributes = attributes,
                UnauthenticatedAttributes = IntPtr.Zero
            }, signatureInfo, false);

            var hr = WinUsbDriverNativeMethods.SignerSignEx(0, subjectInfo, signerCert, signatureInfo,
                IntPtr.Zero, null, IntPtr.Zero, IntPtr.Zero, out var signerContext);
            if (hr != 0)
            {
                return $"SignerSignEx failed for '{filePath}' with HRESULT 0x{hr:X8}";
            }

            if (signerContext != IntPtr.Zero)
            {
                _ = WinUsbDriverNativeMethods.SignerFreeSignerContext(signerContext);
            }

            GC.KeepAlive(certificate);
            return null;
        }
        finally
        {
            foreach (var block in heap)
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    private static IntPtr BuildAuthenticatedAttributes(List<IntPtr> heap)
    {
        var opusOid = Marshal.StringToHGlobalAnsi(SpOpusInfoOid);
        var statementOid = Marshal.StringToHGlobalAnsi(StatementTypeOid);
        heap.Add(opusOid);
        heap.Add(statementOid);

        var opusData = AllocBytes(heap, SpOpusInfoData);
        var statementData = AllocBytes(heap, StatementTypeData);
        var opusBlob = AllocStructure<CryptDataBlob>(heap);
        Marshal.StructureToPtr(new CryptDataBlob
        {
            DataBytes = (uint)SpOpusInfoData.Length,
            Data = opusData
        }, opusBlob, false);
        var statementBlob = AllocStructure<CryptDataBlob>(heap);
        Marshal.StructureToPtr(new CryptDataBlob
        {
            DataBytes = (uint)StatementTypeData.Length,
            Data = statementData
        }, statementBlob, false);

        var opusBlobArray = AllocPointerArray(heap, [opusBlob]);
        var statementBlobArray = AllocPointerArray(heap, [statementBlob]);

        var opusAttribute = AllocStructure<CryptAttribute>(heap);
        Marshal.StructureToPtr(new CryptAttribute
        {
            ObjectId = opusOid,
            ValueCount = 1,
            Values = opusBlobArray
        }, opusAttribute, false);
        var statementAttribute = AllocStructure<CryptAttribute>(heap);
        Marshal.StructureToPtr(new CryptAttribute
        {
            ObjectId = statementOid,
            ValueCount = 1,
            Values = statementBlobArray
        }, statementAttribute, false);

        var attributeArray = AllocPointerArray(heap, [opusAttribute, statementAttribute]);
        var attributes = AllocStructure<CryptAttributes>(heap);
        Marshal.StructureToPtr(new CryptAttributes
        {
            AttributeCount = 2,
            Attributes = attributeArray
        }, attributes, false);
        return attributes;
    }

    private static string? TryVerifyCatalog(string catalogPath)
    {
        var pathPointer = Marshal.StringToHGlobalUni(catalogPath);
        try
        {
            var fileInfo = new WinUsbDriverNativeMethods.WinTrustFileInfo
            {
                CbSize = (uint)Marshal.SizeOf<WinUsbDriverNativeMethods.WinTrustFileInfo>(),
                FilePath = pathPointer,
                File = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            };
            var data = new WinUsbDriverNativeMethods.WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinUsbDriverNativeMethods.WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                Union = Marshal.AllocHGlobal(Marshal.SizeOf<WinUsbDriverNativeMethods.WinTrustFileInfo>())
            };
            try
            {
                Marshal.StructureToPtr(fileInfo, data.Union, false);
                data.StateAction = 1;
                var verifyResult = WinUsbDriverNativeMethods.WinVerifyTrust(IntPtr.Zero,
                    WinUsbDriverNativeMethods.WinTrustActionGenericVerifyV2, ref data);
                if (verifyResult == 0)
                {
                    return null;
                }

                return $"WinVerifyTrust reported 0x{verifyResult:X8} for the signed catalog";
            }
            finally
            {
                if (data.StateData != IntPtr.Zero)
                {
                    data.StateAction = 2;
                    _ = WinUsbDriverNativeMethods.WinVerifyTrust(IntPtr.Zero,
                        WinUsbDriverNativeMethods.WinTrustActionGenericVerifyV2, ref data);
                }

                Marshal.FreeHGlobal(data.Union);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    private static IntPtr AllocBytes(List<IntPtr> heap, int size)
    {
        var block = Marshal.AllocHGlobal(size);
        heap.Add(block);
        return block;
    }

    private static IntPtr AllocBytes(List<IntPtr> heap, byte[] data)
    {
        var block = Marshal.AllocHGlobal(data.Length);
        heap.Add(block);
        Marshal.Copy(data, 0, block, data.Length);
        return block;
    }

    private static IntPtr AllocStructure<T>(List<IntPtr> heap)
        where T : struct
    {
        var block = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        heap.Add(block);
        return block;
    }

    private static IntPtr AllocPointerArray(List<IntPtr> heap, IntPtr[] pointers)
    {
        var block = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        heap.Add(block);
        for (var i = 0; i < pointers.Length; i++)
        {
            Marshal.WriteIntPtr(block, i * IntPtr.Size, pointers[i]);
        }

        return block;
    }

    /// <summary>
    /// The native <c>SIGNER_FILE_INFO</c> as laid out in mssign32.
    /// <para>mssign32 中原生 <c>SIGNER_FILE_INFO</c> 的布局。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SignerFileInfo
    {
        public uint CbSize;
        public IntPtr FileName;
        public IntPtr File;
    }

    /// <summary>
    /// The native <c>SIGNER_SUBJECT_INFO</c>; the union collapses to one pointer-sized field.
    /// <para>原生 <c>SIGNER_SUBJECT_INFO</c>;联合体收敛为单个指针大小字段。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SignerSubjectInfo
    {
        public uint CbSize;
        public IntPtr Index;
        public uint SubjectChoice;
        public IntPtr Subject;
    }

    /// <summary>
    /// The native <c>SIGNER_CERT_STORE_INFO</c>.
    /// <para>原生 <c>SIGNER_CERT_STORE_INFO</c>。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SignerCertStoreInfo
    {
        public uint CbSize;
        public IntPtr SigningCert;
        public uint CertPolicy;
        public IntPtr CertStore;
    }

    /// <summary>
    /// The native <c>SIGNER_CERT</c>; the union collapses to one pointer-sized field.
    /// <para>原生 <c>SIGNER_CERT</c>;联合体收敛为单个指针大小字段。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SignerCert
    {
        public uint CbSize;
        public uint CertChoice;
        public IntPtr StoreInfo;
        public IntPtr Hwnd;
    }

    /// <summary>
    /// The native <c>SIGNER_SIGNATURE_INFO</c>.
    /// <para>原生 <c>SIGNER_SIGNATURE_INFO</c>。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SignerSignatureInfo
    {
        public uint CbSize;
        public uint HashAlgorithm;
        public uint AttrChoice;
        public IntPtr AttrAuthcode;
        public IntPtr AuthenticatedAttributes;
        public IntPtr UnauthenticatedAttributes;
    }

    /// <summary>
    /// The native <c>CRYPT_DATA_BLOB</c> used for attribute payloads.
    /// <para>用于属性负载的原生 <c>CRYPT_DATA_BLOB</c>。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptDataBlob
    {
        public uint DataBytes;
        public IntPtr Data;
    }

    /// <summary>
    /// The native <c>CRYPT_ATTRIBUTE</c>: an ANSI object identifier with one value blob.
    /// <para>原生 <c>CRYPT_ATTRIBUTE</c>:一个 ANSI 对象标识符与单个值 blob。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttribute
    {
        public IntPtr ObjectId;
        public uint ValueCount;
        public IntPtr Values;
    }

    /// <summary>
    /// The native <c>CRYPT_ATTRIBUTES</c> array header.
    /// <para>原生 <c>CRYPT_ATTRIBUTES</c> 数组头。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttributes
    {
        public uint AttributeCount;
        public IntPtr Attributes;
    }
}