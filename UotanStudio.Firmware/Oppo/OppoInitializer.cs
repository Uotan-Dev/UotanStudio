using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Diagnostics;
using UotanStudio.Firmware.Oppo.Models;
using UotanStudio.Firmware.Oppo.Parsers;

namespace UotanStudio.Firmware.Oppo;

/// <summary>
/// <para>提供 Oppo 格式的初始化功能，负责注册各格式的解析器、加密提供者和增强格式检测器。</para>
/// Provides initialization for the Oppo formats, registering parsers, crypto providers, and the enhanced format detector.
/// </summary>
public static class OppoInitializer
{
    private static readonly Lock Lock = new();
    private static bool _initialized;

    /// <summary>
    /// <para>获取全局共享的加密提供者聚合器实例。</para>
    /// Gets the globally shared crypto provider aggregator instance.
    /// </summary>
    public static OppCryptoProvider CryptoProvider { get; } = new OppCryptoProvider(NullOppLogger.Instance);

    /// <summary>
    /// <para>确保各 Oppo 格式的解析器、加密提供者和格式检测器均已注册。</para>
    /// Ensures parsers, crypto providers, and format detectors for all Oppo formats are registered.
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (Lock)
        {
            if (_initialized)
            {
                return;
            }

            // Create and register format-specific crypto providers
            var qcProvider = new OfpQcCryptoProvider(NullOppLogger.Instance);
            var mtkProvider = new OfpMtkCryptoProvider(NullOppLogger.Instance);
            var opsProvider = new OpsFormatCryptoProvider();
            var ozipProvider = new OzipFormatCryptoProvider();

            CryptoProvider.RegisterProvider(qcProvider);
            CryptoProvider.RegisterProvider(mtkProvider);
            CryptoProvider.RegisterProvider(opsProvider);
            CryptoProvider.RegisterProvider(ozipProvider);

            // Register parsers with the core registry
            ParserRegistry.Register(new OfpQcFormatParser(qcProvider));
            ParserRegistry.Register(new OfpMtkFormatParser(mtkProvider));
            ParserRegistry.Register(new OpsFormatParser());
            ParserRegistry.Register(new OzipFormatParser());

            // Register enhanced MTK format detector
            FormatDetector.RegisterDetector(TryDetectMtkFormat);

            _initialized = true;
        }
    }

    /// <summary>
    /// <para>使用 MtkShuffle 密钥尝试检测 MTK 混淆头格式。</para>
    /// Attempts to detect MTK obfuscated header format using the MtkShuffle key.
    /// </summary>
    /// <param name="stream"><para>可读取的文件流。</para>A readable file stream.</param>
    /// <returns><para>若检测到 MTK 格式返回 <see cref="OppFormat.OfpMtk"/>；否则返回 null。</para><see cref="OppFormat.OfpMtk"/> if MTK format is detected; otherwise null.</returns>
    private static OppFormat? TryDetectMtkFormat(Stream stream)
    {
        try
        {
            var fileSize = stream.Length;
            if (fileSize < OfpMtkHeader.Size)
            {
                return null;
            }

            var originalPosition = stream.Position;
            try
            {
                stream.Position = fileSize - OfpMtkHeader.Size;
                var headerBuffer = new byte[OfpMtkHeader.Size];
                var bytesRead = stream.Read(headerBuffer, 0, OfpMtkHeader.Size);
                if (bytesRead != OfpMtkHeader.Size)
                {
                    return null;
                }

                // Apply MtkShuffle deobfuscation
                OppMtkKeyDerivation.MtkShuffle(MtkKeyDatabase.MtkHeaderShuffleKey, headerBuffer);

                // Check if the first 46 bytes are printable ASCII after deobfuscation
                if (FormatDetector.IsPrintableAscii(headerBuffer.AsSpan(0, 46)))
                {
                    return OppFormat.OfpMtk;
                }

                // Also check if the deobfuscated header is all zeros.
                return FormatDetector.IsAllZeros(headerBuffer.AsSpan(0, 46)) ? OppFormat.OfpMtk : null;
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }
        catch
        {
            return null;
        }
    }
}