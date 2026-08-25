using System;
using System.Collections.Generic;

namespace UotanStudio.Services;

/// <summary>
/// Parsed command-line options for the desktop app: an optional solution to open, files to import, and
/// help/version flags. AOT-friendly (no reflection).
/// <para>桌面应用的命令行解析结果：可选的要打开的解决方案、要导入的文件以及帮助/版本标志。AOT 友好（无反射）。</para>
/// </summary>
public sealed class CommandLineOptions
{
    /// <summary>
    /// Path of a <c>.uotan</c> solution to open on startup; <c>null</c> when none given.
    /// <para>启动时要打开的 <c>.uotan</c> 解决方案路径；未给出时为 <c>null</c>。</para>
    /// </summary>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// Files to import into the opened solution on startup.
    /// <para>启动时要导入到已打开解决方案的文件。</para>
    /// </summary>
    public List<string> ImportFiles { get; } = [];

    /// <summary>
    /// Whether <c>--help</c>/<c>-h</c> was requested.
    /// <para>是否请求了 <c>--help</c>/<c>-h</c>。</para>
    /// </summary>
    public bool HelpRequested { get; set; }

    /// <summary>
    /// Whether <c>--version</c>/<c>-v</c> was requested.
    /// <para>是否请求了 <c>--version</c>/<c>-v</c>。</para>
    /// </summary>
    public bool VersionRequested { get; set; }

    /// <summary>
    /// Whether <c>--register-file-associations</c> was requested.
    /// <para>是否请求了 <c>--register-file-associations</c>。</para>
    /// </summary>
    public bool RegisterAssociations { get; set; }

    /// <summary>
    /// Whether <c>--unregister-file-associations</c> was requested.
    /// <para>是否请求了 <c>--unregister-file-associations</c>。</para>
    /// </summary>
    public bool UnregisterAssociations { get; set; }
}

/// <summary>
/// Hand-rolled command-line parser (AOT-friendly, no reflection). Supported forms:
/// <list type="bullet">
/// <item><c>--open &lt;path&gt;</c> / <c>-o &lt;path&gt;</c> — open a <c>.uotan</c> solution.</item>
/// <item><c>--import &lt;file&gt;</c> / <c>-i &lt;file&gt;</c> (repeatable) — import a file.</item>
/// <item>positional <c>&lt;path&gt;.uotan</c> — open a solution; other positional files are imported.</item>
/// <item><c>--help</c>/<c>-h</c> and <c>--version</c>/<c>-v</c>.</item>
/// </list>
/// <para>手写命令行解析器（AOT 友好，无反射）。支持形式：
/// <c>--open &lt;path&gt;</c> / <c>-o &lt;path&gt;</c> 打开 <c>.uotan</c> 解决方案；
/// <c>--import &lt;file&gt;</c> / <c>-i &lt;file&gt;</c>（可多次）导入文件；
/// 位置参数 <c>&lt;path&gt;.uotan</c> 打开解决方案，其余位置参数视为导入文件；
/// 以及 <c>--help</c>/<c>-h</c> 与 <c>--version</c>/<c>-v</c>。</para>
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    /// Parses the given arguments into a <see cref="CommandLineOptions"/> instance.
    /// <para>将给定参数解析为 <see cref="CommandLineOptions"/> 实例。</para>
    /// </summary>
    public static CommandLineOptions Parse(string[]? args)
    {
        var options = new CommandLineOptions();
        if (args is null)
        {
            return options;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    options.HelpRequested = true;
                    break;
                case "--version":
                case "-v":
                    options.VersionRequested = true;
                    break;
                case "--register-file-associations":
                    options.RegisterAssociations = true;
                    break;
                case "--unregister-file-associations":
                    options.UnregisterAssociations = true;
                    break;
                case "--open":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        options.ProjectPath = args[++i];
                    }

                    break;
                case "--import":
                case "-i":
                    if (i + 1 < args.Length)
                    {
                        options.ImportFiles.Add(args[++i]);
                    }

                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        // 未知标志：忽略。
                        // Unknown flag: ignore.
                        break;
                    }

                    if (options.ProjectPath is null && arg.EndsWith(".uotan", StringComparison.OrdinalIgnoreCase))
                    {
                        options.ProjectPath = arg;
                    }
                    else
                    {
                        options.ImportFiles.Add(arg);
                    }

                    break;
            }
        }

        return options;
    }
}