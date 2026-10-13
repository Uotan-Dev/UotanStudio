using System.Buffers;

namespace UotanStudio.Services;

/// <summary>Caches the illegal file and path character sets as accelerated search values so validation avoids reallocating arrays per call.<para>将非法文件名与路径字符集缓存为加速搜索值，使校验无需每次调用重新分配数组。</para></summary>
internal static class PathSearchValues
{
    /// <summary>The characters rejected in file names, from <see cref="Path.GetInvalidFileNameChars"/> and including directory separators.<para>来自 <see cref="Path.GetInvalidFileNameChars"/> 且在文件名中被拒绝的字符集，含目录分隔符。</para></summary>
    /// <value>Accelerated search values over the cached set.<para>基于缓存字符集的加速搜索值。</para></value>
    internal static SearchValues<char> InvalidFileName { get; } = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>The characters rejected in paths, from <see cref="Path.GetInvalidPathChars"/>.<para>来自 <see cref="Path.GetInvalidPathChars"/> 且在路径中被拒绝的字符集。</para></summary>
    /// <value>Accelerated search values over the cached set.<para>基于缓存字符集的加速搜索值。</para></value>
    internal static SearchValues<char> InvalidPath { get; } = SearchValues.Create(Path.GetInvalidPathChars());

    /// <summary>Checks whether a single character is rejected in file or path names.<para>判断单个字符是否为文件名或路径中的非法字符。</para></summary>
    /// <param name="value">The character to test.<para>要测试的字符。</para></param>
    /// <returns>True when the character is illegal.<para>字符非法时为 true。</para></returns>
    internal static bool IsInvalidFileNameChar(char value) => InvalidFileName.Contains(value);
}