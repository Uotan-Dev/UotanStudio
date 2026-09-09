namespace UotanStudio.Media.Audio;

/// <summary>Shared bounded PCM and AAC configuration validation.</summary>
internal static class AudioFormatLimits
{
    internal const int MaximumPacketBytes = 16 * 1024 * 1024;
    internal static void Validate(int channels, int rate, int bits)
    {
        if (channels is < 1 or > 8 || rate is < 7350 or > 192000 || bits is not (16 or 32))
            throw new ArgumentOutOfRangeException(nameof(channels), "Unsupported PCM format.");
    }
    internal static (int Rate, int Channels) ParseAacConfig(ReadOnlySpan<byte> config)
    {
        if (config.Length is < 2 or > 64) throw new InvalidDataException("Invalid AAC AudioSpecificConfig length.");
        int objectType = config[0] >> 3;
        int index = ((config[0] & 7) << 1) | (config[1] >> 7);
        ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int rate, channels;
        if (index == 15)
        {
            if (config.Length < 5) throw new InvalidDataException("Truncated explicit AAC sample rate.");
            rate = ((config[1] & 127) << 17) | (config[2] << 9) | (config[3] << 1) | (config[4] >> 7);
            channels = (config[4] >> 3) & 15;
        }
        else
        {
            if (index >= rates.Length) throw new InvalidDataException("Reserved AAC sample rate.");
            rate = rates[index];
            channels = (config[1] >> 3) & 15;
        }
        // scrcpy's AAC encoder emits AAC-LC. PCE/SBR/PS need a different format negotiation path.
        if (objectType != 2 || channels is < 1 or > 7) throw new NotSupportedException("Expected AAC-LC with explicit channel configuration.");
        if (channels == 7) channels = 8;
        Validate(channels, rate, 16);
        return (rate, channels);
    }
}