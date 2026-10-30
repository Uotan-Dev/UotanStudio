using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The native boundary used to build a self-signed WinUSB driver package: CryptoAPI certificate
/// creation and store management, catalog assembly through <c>wintrust</c>, Authenticode signing
/// through <c>mssign32</c>, and the setupapi driver-store calls that stage and remove the package.
/// Every shape mirrors the corresponding <c>wincrypt.h</c>, <c>mssign32.h</c>, <c>wintrust.h</c> and
/// <c>softpub.h</c> layouts byte for byte.
/// <para>构建自签名 WinUSB 驱动包所用的原生边界：CryptoAPI 证书创建与存储管理、经 <c>wintrust</c>
/// 组装编录文件、经 <c>mssign32</c> 做 Authenticode 签名，以及暂存与移除驱动包的 setupapi 调用。
/// 所有结构都与 <c>wincrypt.h</c>、<c>mssign32.h</c>、<c>wintrust.h</c> 与 <c>softpub.h</c> 逐字节对齐。</para>
/// </summary>
internal static unsafe partial class WinUsbTakeoverNativeMethods
{
    internal const uint CertStoreProvSystem = 10;
    internal const uint CertSystemStoreLocalMachine = 0x00020000;
    internal const uint CertSystemStoreCurrentUser = 0x00010000;
    internal const uint X509AsnEncoding = 0x00010001;
    internal const uint CertX500NameStr = 0x00040000;
    internal const uint CertStoreAddReplaceExisting = 3;
    internal const uint CertFindSubjectName = 0x000b0000;
    internal const uint CertFriendlyNamePropId = 9;

    internal const uint CryptMachineKeyset = 0x00000020;
    internal const uint CryptSilent = 0x00000040;
    internal const uint CryptNewKeyset = 0x00000008;
    internal const uint CryptDeleteKeyset = 0x00000010;
    internal const uint CryptVerifyContext = 0xF0000000;
    internal const uint ProvRsaFull = 1;
    internal const uint AtSignature = 2;
    internal const uint CryptExportable = 0x00000001;
    internal const uint KeySize2048 = 2048 << 16;

    internal const uint CalgSha256 = 0x0000800c;

    internal const uint CryptCatOpenCreateNew = 0x00000001;
    internal const uint CryptCatAttrAuthenticated = 0x10000000;
    internal const uint CryptCatAttrNameAscii = 0x00000001;
    internal const uint CryptCatAttrDataAscii = 0x00010000;
    internal const uint CryptCatMemberVersion = 0x200;
    internal const uint SpcFileLinkChoice = 1;
    internal const int Sha1HashLength = 20;

    internal const uint SignerSubjectFile = 1;
    internal const uint SignerCertStore = 2;
    internal const uint SignerCertPolicyChain = 2;
    internal const uint SignerNoAttr = 0;

    internal const string SpcCabDataObjId = "1.3.6.1.4.1.311.2.1.25";
    internal const string Sha1ObjId = "1.3.14.3.2.26";
    internal const string EnhancedKeyUsageObjId = "2.5.29.37";
    internal const string CodeSigningUsageObjId = "1.3.6.1.5.5.7.3.3";
    internal const string Sha256WithRsaObjId = "1.2.840.113549.1.1.11";
    internal const string CatOperatingSystemAttribute = "7_X86,7_X64,8_X86,8_X64,8_ARM,10_X86,10_X64,10_ARM";

    /// <summary>The <c>SPC_INDIRECT_DATA</c> member payload a catalog entry carries.
    /// <para>编录条目携带的 <c>SPC_INDIRECT_DATA</c> 负载。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpcIndirectData
    {
        internal CryptAttributeTypeValue Data;
        internal CryptAlgorithmIdentifier DigestAlgorithm;
        internal CryptDataBlob Digest;
    }

    /// <summary>The <c>CRYPT_ATTRIBUTE_TYPE_VALUE</c> header: an object id plus an encoded value.
    /// <para><c>CRYPT_ATTRIBUTE_TYPE_VALUE</c> 头部：对象标识加编码后的值。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CryptAttributeTypeValue
    {
        internal IntPtr ObjectId;
        internal CryptDataBlob Value;
    }

    /// <summary>The <c>CRYPT_ALGORITHM_IDENTIFIER</c> shape used by signing and catalog structures.
    /// <para>签名与编录结构使用的 <c>CRYPT_ALGORITHM_IDENTIFIER</c> 形状。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CryptAlgorithmIdentifier
    {
        internal IntPtr ObjectId;
        internal CryptDataBlob Parameters;
    }

    /// <summary>A length plus unmanaged pointer pair, matching <c>CRYPT_DATA_BLOB</c> and every blob alias.
    /// <para>长度加非托管指针的组合，与 <c>CRYPT_DATA_BLOB</c> 及其全部别名一致。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CryptDataBlob
    {
        internal uint Length;
        internal IntPtr Data;
    }

    /// <summary>The <c>CRYPT_KEY_PROV_INFO</c> that binds a self-signed certificate to its key container.
    /// <para>把自签证书绑定到密钥容器的 <c>CRYPT_KEY_PROV_INFO</c>。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CryptKeyProvInfo
    {
        internal IntPtr ContainerName;
        internal IntPtr ProviderName;
        internal uint ProviderType;
        internal uint Flags;
        internal uint ParameterCount;
        internal IntPtr Parameters;
        internal uint KeySpec;
    }

    /// <summary>One encoded certificate extension, matching <c>CERT_EXTENSION</c>.
    /// <para>一个编码后的证书扩展，与 <c>CERT_EXTENSION</c> 一致。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CertExtension
    {
        internal IntPtr ObjectId;
        internal int Critical;
        internal CryptDataBlob Value;
    }

    /// <summary>The extension array handed to <c>CertCreateSelfSignCertificate</c>.
    /// <para>传给 <c>CertCreateSelfSignCertificate</c> 的扩展数组。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CertExtensions
    {
        internal uint Count;
        internal IntPtr Extensions;
    }

    /// <summary>The <c>CERT_ENHKEY_USAGE</c> payload encoded into the key usage extension.
    /// <para>编码进密钥用法扩展的 <c>CERT_ENHKEY_USAGE</c> 负载。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CertEnhKeyUsage
    {
        internal uint Count;
        internal IntPtr Usages;
    }

    /// <summary>The <c>SPC_LINK</c> structure that every catalog member carries as an obsolete link.
    /// <para>每个编录条目都要携带的过时链接结构 <c>SPC_LINK</c>。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpcLink
    {
        internal uint LinkChoice;
        internal IntPtr Url;
    }

    /// <summary>The <c>SIGNER_FILE_INFO</c> describing the file being signed.
    /// <para>描述被签名文件的 <c>SIGNER_FILE_INFO</c>。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerFileInfo
    {
        internal uint Size;
        internal IntPtr FileName;
        internal IntPtr File;
    }

    /// <summary>The <c>SIGNER_SUBJECT_INFO</c> selecting the file subject.
    /// <para>选择文件主体的 <c>SIGNER_SUBJECT_INFO</c>。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerSubjectInfo
    {
        internal uint Size;
        internal IntPtr Index;
        internal uint SubjectChoice;
        internal IntPtr Subject;
    }

    /// <summary>The <c>SIGNER_CERT_STORE_INFO</c> describing the signing certificate.
    /// <para>描述签名证书的 <c>SIGNER_CERT_STORE_INFO</c>。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerCertStoreInfo
    {
        internal uint Size;
        internal IntPtr SigningCert;
        internal uint CertPolicy;
        internal IntPtr CertStore;
    }

    /// <summary>The <c>SIGNER_CERT</c> wrapper selecting the store-backed certificate.
    /// <para>选择存储证书的 <c>SIGNER_CERT</c> 包装。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerCert
    {
        internal uint Size;
        internal uint CertChoice;
        internal IntPtr CertInfo;
        internal IntPtr Window;
    }

    /// <summary>The <c>SIGNER_SIGNATURE_INFO</c> selecting the digest algorithm and attributes.
    /// <para>选择摘要算法与属性的 <c>SIGNER_SIGNATURE_INFO</c>。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SignerSignatureInfo
    {
        internal uint Size;
        internal uint HashAlgorithm;
        internal uint AttributeChoice;
        internal IntPtr Attribute;
        internal IntPtr Authenticated;
        internal IntPtr Unauthenticated;
    }

    /// <summary>
    /// Acquires one CSP context. The provider calls of this class are exported by <c>advapi32</c>, not by
    /// <c>crypt32</c>, which only carries the certificate and catalog APIs here.
    /// <para>获取一个 CSP 上下文。本类的加密提供者调用由 <c>advapi32</c> 导出，<c>crypt32</c> 只提供这里的
    /// 证书与编录 API。</para>
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true, EntryPoint = "CryptAcquireContextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptAcquireContext(out IntPtr provider, IntPtr container, IntPtr providerName,
        uint providerType, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptGenKey(IntPtr provider, uint algorithm, uint flags, out IntPtr key);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptDestroyKey(IntPtr key);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptReleaseContext(IntPtr provider, uint flags);

    [LibraryImport("crypt32.dll", SetLastError = true, EntryPoint = "CertStrToNameW",
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertStrToName(uint encoding, string name, uint stringType, IntPtr reserved,
        byte* encoded, ref uint encodedSize, IntPtr error);

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptEncodeObject(uint encoding, string structureType, void* structure,
        byte* encoded, ref uint encodedSize);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    internal static partial IntPtr CertCreateSelfSignCertificate(IntPtr provider, CryptDataBlob* subject,
        uint flags, CryptKeyProvInfo* keyProvInfo, CryptAlgorithmIdentifier* signatureAlgorithm,
        void* startTime, void* endTime, CertExtensions* extensions);

    [LibraryImport("crypt32.dll", SetLastError = true, EntryPoint = "CertOpenStore")]
    internal static partial IntPtr CertOpenStore(IntPtr storeProvider, uint encoding, IntPtr provider, uint flags,
        IntPtr parameter);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertAddCertificateContextToStore(IntPtr store, IntPtr certificate, uint disposition,
        IntPtr storeContext);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertAddEncodedCertificateToStore(IntPtr store, uint encoding, byte* encoded,
        uint encodedSize, uint disposition, out IntPtr storeContext);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertSetCertificateContextProperty(IntPtr certificate, uint propertyId, uint flags,
        CryptDataBlob* data);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    internal static partial IntPtr CertFindCertificateInStore(IntPtr store, uint encoding, uint findFlags,
        uint findType, CryptDataBlob* findParameter, IntPtr previous);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertDeleteCertificateFromStore(IntPtr certificate);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertFreeCertificateContext(IntPtr certificate);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertCloseStore(IntPtr store, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true, EntryPoint = "CryptCATOpen")]
    internal static partial IntPtr CryptCatOpen(IntPtr catalogFile, uint openFlags, IntPtr provider,
        uint publicVersion, uint encoding);

    [LibraryImport("wintrust.dll", SetLastError = true, EntryPoint = "CryptCATPutCatAttrInfo")]
    internal static partial IntPtr CryptCatPutCatAttribute(IntPtr catalog, IntPtr referenceTag, uint attributeType,
        uint size, byte* data);

    [LibraryImport("wintrust.dll", SetLastError = true, EntryPoint = "CryptCATPutMemberInfo")]
    internal static partial IntPtr CryptCatPutMember(IntPtr catalog, IntPtr fileName, IntPtr referenceTag,
        Guid* subjectType, uint version, uint size, byte* data);

    [LibraryImport("wintrust.dll", SetLastError = true, EntryPoint = "CryptCATPutAttrInfo")]
    internal static partial IntPtr CryptCatPutMemberAttribute(IntPtr catalog, IntPtr member, IntPtr referenceTag,
        uint attributeType, uint size, byte* data);

    [LibraryImport("wintrust.dll", SetLastError = true, EntryPoint = "CryptCATPersistStore")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCatPersistStore(IntPtr catalog);

    [LibraryImport("wintrust.dll", SetLastError = true, EntryPoint = "CryptCATClose")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCatClose(IntPtr catalog);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminCalcHashFromFileHandle(IntPtr file, ref uint hashSize, byte* hash,
        uint flags);

    [LibraryImport("mssign32.dll", SetLastError = true, EntryPoint = "SignerSignEx")]
    internal static partial int SignerSignEx(uint flags, SignerSubjectInfo* subject, SignerCert* certificate,
        SignerSignatureInfo* signature, IntPtr provider, IntPtr timestampUrl, IntPtr request, IntPtr sipData,
        out IntPtr signerContext);

    [LibraryImport("mssign32.dll", SetLastError = true)]
    internal static partial int SignerFreeSignerContext(IntPtr signerContext);

    /// <summary>
    /// Encodes one UTF-16 string as a null-terminated unmanaged block the caller must free.
    /// <para>把一个 UTF-16 字符串编码为调用方必须释放的、以 null 结尾的非托管块。</para>
    /// </summary>
    /// <param name="value">The string to encode.<para>要编码的字符串。</para></param>
    /// <returns>The unmanaged pointer, or <see cref="IntPtr.Zero"/> for a null input.
    /// <para>非托管指针；输入为 null 时为 <see cref="IntPtr.Zero"/>。</para></returns>
    internal static IntPtr AllocateUnicode(string? value)
        => value is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(value);

    /// <summary>
    /// Opens a system certificate store by location and name. The system-store provider is the <c>_W</c>
    /// variant, so the name must be a Unicode buffer; an ANSI buffer opens an empty store instead of the
    /// named one.
    /// <para>按位置与名称打开系统证书存储。系统存储提供者是 <c>_W</c> 变体，名称必须是 Unicode 缓冲区；
    /// 传 ANSI 缓冲区会打开一个空存储而不是指定的存储。</para>
    /// </summary>
    /// <param name="location">The store location, for example <see cref="CertSystemStoreLocalMachine"/>.
    /// <para>存储位置，例如 <see cref="CertSystemStoreLocalMachine"/>。</para></param>
    /// <param name="storeName">The store name, for example <c>Root</c>.<para>存储名，例如 <c>Root</c>。</para></param>
    /// <returns>The store handle, or <see cref="IntPtr.Zero"/> on failure.
    /// <para>存储句柄；失败时为 <see cref="IntPtr.Zero"/>。</para></returns>
    internal static IntPtr OpenStore(uint location, string storeName)
    {
        var name = AllocateUnicode(storeName);
        try
        {
            return CertOpenStore((nint)CertStoreProvSystem, X509AsnEncoding, IntPtr.Zero, location, name);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>
    /// Formats the last Win32 error as a human-readable message.
    /// <para>把最近的 Win32 错误格式化为可读消息。</para>
    /// </summary>
    /// <param name="prefix">Optional context prepended to the message.<para>可选的上下文前缀。</para></param>
    /// <returns>The formatted message.<para>格式化后的消息。</para></returns>
    internal static string DescribeLastError(string prefix)
        => WinUsbDriverNativeMethods.DescribeLastWin32Error(prefix);

    /// <summary>
    /// Decodes a hexadecimal SHA-1 digest as the uppercase reference tag a catalog member requires.
    /// <para>把 SHA-1 十六进制摘要解码为编录条目要求的全大写引用标签。</para>
    /// </summary>
    /// <param name="hash">The digest bytes.<para>摘要字节。</para></param>
    /// <returns>The uppercase hexadecimal tag.<para>全大写十六进制标签。</para></returns>
    internal static string ToReferenceTag(ReadOnlySpan<byte> hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            builder.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}