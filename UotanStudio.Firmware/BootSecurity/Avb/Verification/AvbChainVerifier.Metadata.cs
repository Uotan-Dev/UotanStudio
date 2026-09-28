using UotanStudio.Firmware.BootSecurity.Avb;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

public static partial class AvbChainVerifier
{
    private sealed partial class Context
    {
        private async Task<(AvbVBMetaImageHeader Header, byte[] Bytes)> ReadMetadataAsync(Stream stream)
        {
            if (stream.Length < AvbFooter.Size)
            {
                throw new InvalidDataException("The image cannot contain an AVB header or footer.");
            }
            var footerBytes = new byte[AvbFooter.Size];
            stream.Position = stream.Length - AvbFooter.Size;
            await stream.ReadExactlyAsync(footerBytes, _token).ConfigureAwait(false);
            var footer = AvbFooter.TryLoad(footerBytes);
            if (footer is null && footerBytes.AsSpan(0, 4).SequenceEqual("AVBf"u8))
            {
                throw new InvalidDataException("The AVB footer version is unsupported.");
            }
            var offset = 0L;
            var available = (ulong)stream.Length;
            var footerBased = footer.HasValue;
            if (footer is { } metadataFooter)
            {
                var end = (ulong)stream.Length - AvbFooter.Size;
                if (metadataFooter.OriginalImageSize > metadataFooter.VbmetaOffset || metadataFooter.VbmetaOffset > end || metadataFooter.VbmetaSize > end - metadataFooter.VbmetaOffset)
                {
                    throw new InvalidDataException("The AVB footer overlaps content or references bytes outside the image.");
                }
                offset = (long)metadataFooter.VbmetaOffset;
                available = metadataFooter.VbmetaSize;
            }
            if (available < AvbVBMetaImageHeader.Size)
            {
                throw new InvalidDataException("The VBMeta header is truncated.");
            }
            stream.Position = offset;
            var headerBytes = new byte[AvbVBMetaImageHeader.Size];
            await stream.ReadExactlyAsync(headerBytes, _token).ConfigureAwait(false);
            var header = AvbVBMetaImageHeader.FromBytes(headerBytes);
            if (!header.IsValid() || header.RequiredLibavbVersionMajor != 1 || header.RequiredLibavbVersionMinor > 3)
            {
                throw new InvalidDataException("The VBMeta magic or required libavb version is unsupported.");
            }
            const ulong maximumBlock = 16 * 1024 * 1024;
            var auth = header.AuthenticationDataBlockSize;
            var auxiliary = header.AuxiliaryDataBlockSize;
            if (auth > maximumBlock || auxiliary > maximumBlock || auth % 64 != 0 || auxiliary % 64 != 0
                || auth > available - AvbVBMetaImageHeader.Size || auxiliary > available - AvbVBMetaImageHeader.Size - auth)
            {
                throw new InvalidDataException("VBMeta blocks exceed the image, 16 MiB per-block limit or required alignment.");
            }
            var length = checked(AvbVBMetaImageHeader.Size + (int)auth + (int)auxiliary);
            if (footerBased && (ulong)length != available)
            {
                throw new InvalidDataException("The footer VBMeta size does not match its declared blocks.");
            }
            if (!Within(header.HashOffset, header.HashSize, auth) || !Within(header.SignatureOffset, header.SignatureSize, auth)
                || !Within(header.PublicKeyOffset, header.PublicKeySize, auxiliary)
                || !Within(header.PublicKeyMetadataOffset, header.PublicKeyMetadataSize, auxiliary)
                || !Within(header.DescriptorsOffset, header.DescriptorsSize, auxiliary))
            {
                throw new InvalidDataException("A VBMeta field exceeds its containing block.");
            }
            _metadataBytes = checked(_metadataBytes + length);
            if (_metadataBytes > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("The AVB graph exceeds the 64 MiB cumulative metadata limit.");
            }
            var bytes = new byte[length];
            headerBytes.CopyTo(bytes, 0);
            await stream.ReadExactlyAsync(bytes.AsMemory(AvbVBMetaImageHeader.Size), _token).ConfigureAwait(false);
            return (header, bytes);
        }

        private static bool Within(ulong offset, ulong size, ulong length) => offset <= length && size <= length - offset;
    }
}