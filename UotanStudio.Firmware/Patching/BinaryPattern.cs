namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Bounded masked byte-pattern search over a seekable firmware stream.
/// <para>在可寻址固件流上进行的有界掩码字节模式搜索。</para>
/// </summary>
public static class BinaryPattern
{
    /// <summary>
    /// Copies a source into a private output and applies equal-length masked replacements.
    /// Overlapping replacement windows are rejected. The caller owns atomic publication and source identity checks.
    /// <para>将源复制到私有输出并应用等长的掩码替换。重叠的替换窗口会被拒绝。调用方负责原子发布与源一致性校验。</para>
    /// </summary>
    /// <param name="source">
    /// Readable, seekable firmware stream supplying the original bytes. <para>提供原始字节的可读、可寻址固件流。</para>
    /// </param>
    /// <param name="output">
    /// Separate writable, seekable stream truncated to hold the modified copy. <para>独立的可写、可寻址流，会被截断以容纳修改后的副本。</para>
    /// </param>
    /// <param name="find">
    /// Hexadecimal pattern bytes to locate. <para>待定位的十六进制模式字节。</para>
    /// </param>
    /// <param name="findMask">
    /// Optional hex mask selecting significant find bits; defaults to all bits when empty. <para>可选的十六进制掩码，用于选取有效匹配位；为空时默认全部位。</para>
    /// </param>
    /// <param name="replacement">
    /// Hex replacement bytes, required to match the find pattern length. <para>十六进制替换字节，长度必须与查找模式一致。</para>
    /// </param>
    /// <param name="replaceMask">
    /// Optional hex mask selecting bytes to overwrite; defaults to all bytes when empty. <para>可选的十六进制掩码，用于选取要覆写的字节；为空时默认全部字节。</para>
    /// </param>
    /// <param name="offset">
    /// Absolute stream offset where the search begins. <para>搜索开始的绝对流偏移。</para>
    /// </param>
    /// <param name="length">
    /// Number of bytes to scan; <c>-1</c> means the remainder of the stream. <para>要扫描的字节数；<c>-1</c> 表示流的剩余部分。</para>
    /// </param>
    /// <param name="alignment">
    /// Match offsets must be a multiple of this value, from <c>1</c> to <c>1048576</c>. <para>匹配偏移必须是此值的倍数，范围 <c>1</c> 到 <c>1048576</c>。</para>
    /// </param>
    /// <param name="skip">
    /// Number of leading matches to ignore, up to <c>100000</c>. <para>要忽略的前导匹配数量，最多 <c>100000</c>。</para>
    /// </param>
    /// <param name="expectedMatches">
    /// Required remaining match count; a differing count aborts, up to <c>100000</c>. <para>要求的剩余匹配数量；数量不符即中止，最多 <c>100000</c>。</para>
    /// </param>
    /// <param name="token">
    /// Cancels reads and writes; the source position must remain valid when cancelled. <para>取消读取与写入；取消时源位置必须保持有效。</para>
    /// </param>
    /// <returns>
    /// The absolute offsets at which replacements were applied.
    /// <para>应用替换所在的绝对偏移列表。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="output"/> is the same as the source or is not writable and seekable.
    /// <para><paramref name="output"/> 与源相同，或不可写、不可寻址。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A hex pattern or mask is malformed or misaligned in length, matches overlap or differ from expectation, or the original bytes change mid-replacement.
    /// <para>十六进制模式或掩码格式错误、长度不符，匹配重叠或与期望不符，或替换过程中原始字节发生变化。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The source was truncated before the copy completed.
    /// <para>源在复制完成前被截断。</para>
    /// </exception>
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
        var mask = string.IsNullOrWhiteSpace(replaceMask) ? [.. Enumerable.Repeat((byte)255, pattern.Length)] : ParseHex(replaceMask);
        var originalMask = string.IsNullOrWhiteSpace(findMask) ? [.. Enumerable.Repeat((byte)255, pattern.Length)] : ParseHex(findMask);
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

    /// <summary>Finds overlapping matches at absolute aligned offsets, skipping the first requested matches. Length -1 means the remaining stream. The remaining match count must equal expectedMatches.
    /// <para>在绝对对齐偏移处查找可重叠的匹配，并跳过前若干次匹配。长度 <c>-1</c> 表示流的剩余部分。剩余匹配数量必须等于 expectedMatches。</para></summary>
    /// <param name="source">Readable, seekable stream scanned for the pattern.
    /// <para>用于扫描模式的可读、可寻址流。</para></param>
    /// <param name="find">Hexadecimal pattern bytes to locate.<para>待定位的十六进制模式字节。</para></param>
    /// <param name="mask">Optional hex mask selecting significant bits; defaults to all bits when empty and must contain one.
    /// <para>可选的十六进制掩码，用于选取有效位；为空时默认全部位，且至少需含一位。</para></param>
    /// <param name="offset">Absolute stream offset where the scan begins.
    /// <para>扫描开始的绝对流偏移。</para></param>
    /// <param name="length">Number of bytes to scan; <c>-1</c> means the remainder of the stream.
    /// <para>要扫描的字节数；<c>-1</c> 表示流的剩余部分。</para></param>
    /// <param name="alignment">Match offsets must be a multiple of this value, from <c>1</c> to <c>1048576</c>.
    /// <para>匹配偏移必须是此值的倍数，范围 <c>1</c> 到 <c>1048576</c>。</para></param>
    /// <param name="skip">Number of leading matches to ignore, up to <c>100000</c>.
    /// <para>要忽略的前导匹配数量，最多 <c>100000</c>。</para></param>
    /// <param name="expectedMatches">Required remaining match count; a differing count aborts, up to <c>100000</c>.
    /// <para>要求的剩余匹配数量；数量不符即中止，最多 <c>100000</c>。</para></param>
    /// <param name="token">Cancels reads periodically during the scan.<para>在扫描期间定期取消读取。</para></param>
    /// <returns>The absolute offsets of the accepted matches, in ascending order.
    /// <para>被接受匹配的绝对偏移，按升序排列。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not readable and seekable.
    /// <para><paramref name="source"/> 不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">A hex pattern or mask is malformed, the range or match limits are out of bounds, or the match count differs from <paramref name="expectedMatches"/>.
    /// <para>十六进制模式或掩码格式错误、范围或匹配上限越界，或匹配数量与 <paramref name="expectedMatches"/> 不符。</para></exception>
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
        var bits = string.IsNullOrWhiteSpace(mask) ? [.. Enumerable.Repeat((byte)255, pattern.Length)] : ParseHex(mask);
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