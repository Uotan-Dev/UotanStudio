namespace UotanStudio.Firmware.AndroidMeta.IO;

internal static class AndroidMetadataReader
{
    internal const int MaximumBytes = 256 * 1024 * 1024;

    internal static byte[] Read(Stream stream, int maximumBytes = MaximumBytes, long? expectedLength = null)
    {
        long? length = expectedLength;
        if (!length.HasValue && stream.CanSeek) length = stream.Length - stream.Position;
        if (length is < 0 || length > maximumBytes)
            throw new InvalidDataException($"Android metadata exceeds the {maximumBytes}-byte limit.");
        if (length is long known)
        {
            var bytes = new byte[(int)known];
            stream.ReadExactly(bytes);
            if (expectedLength.HasValue && stream.ReadByte() != -1)
                throw new InvalidDataException("Android metadata exceeds its declared size.");
            return bytes;
        }
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (read > maximumBytes - output.Length) throw new InvalidDataException("Android metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}