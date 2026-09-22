namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Bounded masked byte-pattern search over a seekable firmware stream.</summary>
public static class BinaryPattern
{
    /// <summary>Copies a source into a private output and applies equal-length masked replacements.
    /// Overlapping replacement windows are rejected. The caller owns atomic publication and source identity checks.</summary>
    public static async Task<IReadOnlyList<long>> ReplaceAsync(Stream source, Stream output, string find, string findMask,
        string replacement, string replaceMask, long offset, long length, int alignment, int skip,
        int expectedMatches, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(source, output) || !output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("A separate writable seekable output is required.", nameof(output));
        }
        var pattern = ParseHex(find);
        var changed = ParseHex(replacement);
        var mask = string.IsNullOrWhiteSpace(replaceMask) ? Enumerable.Repeat((byte)255, pattern.Length).ToArray() : ParseHex(replaceMask);
        var originalMask = string.IsNullOrWhiteSpace(findMask) ? Enumerable.Repeat((byte)255, pattern.Length).ToArray() : ParseHex(findMask);
        if (changed.Length != pattern.Length || mask.Length != pattern.Length)
        {
            throw new InvalidDataException("Binary replacements and masks must preserve the pattern length.");
        }
        var matches = await FindAsync(source, find, findMask, offset, length, alignment, skip, expectedMatches, token).ConfigureAwait(false);
        for (var index = 1; index < matches.Count; index++)
        {
            if (matches[index] - matches[index - 1] < pattern.Length)
            {
                throw new InvalidDataException("Overlapping binary replacement windows are ambiguous.");
            }
        }
        source.Position = 0;
        output.Position = 0;
        output.SetLength(0);
        var remaining = source.Length;
        var copyBuffer = new byte[131072];
        while (remaining > 0)
        {
            var read = await source.ReadAsync(copyBuffer.AsMemory(0, (int)Math.Min(copyBuffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Binary source was truncated while copying.");
            }
            await output.WriteAsync(copyBuffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
        var bytes = new byte[pattern.Length];
        foreach (var match in matches)
        {
            source.Position = match;
            await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (!Matches(bytes, 0, pattern, originalMask))
            {
                throw new InvalidDataException("Original bytes changed during binary replacement.");
            }
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)((bytes[index] & ~mask[index]) | (changed[index] & mask[index]));
            }
            output.Position = match;
            await output.WriteAsync(bytes, token).ConfigureAwait(false);
        }
        return matches;
    }

    /// <summary>Finds overlapping matches at absolute aligned offsets, skipping the first requested matches.
    /// Length -1 means the remaining stream. The remaining match count must equal expectedMatches.</summary>
    public static async Task<IReadOnlyList<long>> FindAsync(Stream source, string find, string mask,
        long offset, long length, int alignment, int skip, int expectedMatches, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("Binary search requires a readable seekable stream.", nameof(source));
        }
        var pattern = ParseHex(find);
        var bits = string.IsNullOrWhiteSpace(mask) ? Enumerable.Repeat((byte)255, pattern.Length).ToArray() : ParseHex(mask);
        if (bits.Length != pattern.Length || bits.All(value => value == 0))
        {
            throw new InvalidDataException("Find mask must match the pattern length and contain a significant bit.");
        }
        if (offset < 0 || offset > source.Length || length < -1 || alignment is < 1 or > 1048576
            || skip is < 0 or > 100000 || expectedMatches is < 0 or > 100000)
        {
            throw new InvalidDataException("Invalid binary search range or match limits.");
        }
        var available = source.Length - offset;
        length = length == -1 ? available : length;
        if (length > available)
        {
            throw new InvalidDataException("Binary search range exceeds the stream.");
        }
        var results = new List<long>();
        var buffer = new byte[131072 + pattern.Length - 1];
        var remaining = Math.Max(0, length - pattern.Length + 1);
        var position = offset;
        var skipped = 0;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var candidates = (int)Math.Min(131072, remaining);
            var bytes = candidates + pattern.Length - 1;
            source.Position = position;
            await source.ReadExactlyAsync(buffer.AsMemory(0, bytes), token).ConfigureAwait(false);
            for (var index = 0; index < candidates; index++)
            {
                if ((index & 4095) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
                var absolute = position + index;
                if (absolute % alignment != 0 || !Matches(buffer, index, pattern, bits))
                {
                    continue;
                }
                if (skipped < skip)
                {
                    skipped++;
                    continue;
                }
                if (results.Count == expectedMatches)
                {
                    throw new InvalidDataException("Binary pattern matched more times than expected.");
                }
                results.Add(absolute);
            }
            position += candidates;
            remaining -= candidates;
        }
        if (skipped != skip || results.Count != expectedMatches)
        {
            throw new InvalidDataException("Binary pattern match count differs from the expected count.");
        }
        return results;
    }

    private static bool Matches(byte[] buffer, int offset, byte[] pattern, byte[] mask)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if ((buffer[offset + index] & mask[index]) != (pattern[index] & mask[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] ParseHex(string text)
    {
        if (text.Length > 16384)
        {
            throw new InvalidDataException("Binary patterns are limited to 4096 bytes.");
        }
        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length is 0 or > 8192 || compact.Length % 2 != 0 || !compact.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Use hexadecimal byte pairs, optionally separated by whitespace.");
        }
        return Convert.FromHexString(compact);
    }
}