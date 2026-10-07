using System.Buffers;

namespace UotanStudio.Services;

/// <summary>缓存路径校验用的非法字符集，避免每次调用 <see cref="Path.GetInvalidFileNameChars"/> 重新分配数组，并用 <see cref="SearchValues{T}"/> 加速扫描。</summary>
internal static class PathSearchValues
{
    /// <summary><see cref="Path.GetInvalidFileNameChars"/> 拒绝的字符集（含目录分隔符）。</summary>
    internal static SearchValues<char> InvalidFileName { get; } = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary><see cref="Path.GetInvalidPathChars"/> 拒绝的字符集。</summary>
    internal static SearchValues<char> InvalidPath { get; } = SearchValues.Create(Path.GetInvalidPathChars());

    /// <summary>判断单个字符是否为非法文件名/路径字符。</summary>
    internal static bool IsInvalidFileNameChar(char value) => InvalidFileName.Contains(value);
}