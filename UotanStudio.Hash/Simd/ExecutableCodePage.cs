using System.Runtime.InteropServices;

namespace UotanStudio.Hash.Simd;

/// <summary>
/// Allocates the executable page backing the runtime-emitted vector kernels on Linux.
/// <para>在 Linux 上为运行时发射的向量内核分配可执行页。</para>
/// </summary>
/// <remarks>
/// The page is a shared <c>memfd</c> mapping pair: the machine code is written through a writable alias that is
/// released before the code executes, and executed through a separate read-execute alias, so no mapping is ever
/// simultaneously writable and executable. A freshly mapped executable alias faults in on the first instruction
/// fetch, which lets the kernel perform its instruction-cache maintenance before the emitted code runs; the kernels
/// are emitted once and never modified afterwards, so the hot path needs no further flushing. RISC-V additionally
/// issues the <c>riscv_flush_icache</c> syscall after the stores for spec-level coherence. Every method returns
/// <see langword="false"/> off Linux or when the libc helpers are unavailable, and callers must then keep the
/// scalar software kernels.
/// <para>页由共享 memfd 的双别名映射构成：机器码经可写别名写入并在执行前释放该别名，经独立的读可执行别名执行，
/// 因此不存在同时可写可执行的映射。新映射的可执行别名在首次取指时缺页，内核会在发射代码运行前完成指令缓存维护；
/// 内核只发射一次且此后不再修改，热路径无需再刷缓存。RISC-V 额外在写入后发起 <c>riscv_flush_icache</c> 系统调用
/// 以满足规范级一致性。所有方法在非 Linux 平台或 libc 辅助函数不可用时返回 <see langword="false"/>，
/// 调用方此时必须保留标量软件内核。</para>
/// </remarks>
internal static unsafe class ExecutableCodePage
{
    private const int ProtectionReadWrite = 0x3;
    private const int ProtectionReadExecute = 0x5;
    private const int MapShared = 0x1;
    private const int RiscVFlushIcacheSyscallNumber = 259;
    private static readonly IntPtr MapFailed = new(-1);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr address, nuint length, int protection, int flags, int fileDescriptor, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr address, nuint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fileDescriptor, long length);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fileDescriptor);

    [DllImport("libc", SetLastError = false)]
    private static extern long syscall(long number, long argument1, long argument2, long argument3);

    /// <summary>
    /// Reads back the executable alias and compares it with the written machine code; a mismatch means the
    /// processor cannot observe the stores from the executable mapping and the page must not be executed.
    /// <para>从可执行别名读回并与已写入机器码比对；不一致意味着处理器无法从可执行映射观察到这些写入，
    /// 该页绝不能被执行。</para>
    /// </summary>
    /// <param name="machineCode">Machine code that was written. <para>已写入的机器码。</para></param>
    /// <param name="executable">Executable alias to verify. <para>待校验的可执行别名。</para></param>
    /// <returns><see langword="true"/> when the executable bytes match. <para>可执行字节一致时为
    /// <see langword="true"/>。</para></returns>
    private static unsafe bool Matches(ReadOnlySpan<byte> machineCode, byte* executable)
    {
        fixed (byte* source = machineCode)
        {
            for (var index = 0; index < machineCode.Length; index++)
            {
                if (source[index] != executable[index])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Gets why the last <see cref="TryAllocate"/> call failed, or <see langword="null"/> when
    /// the last call succeeded or none ran yet.
    /// <para>获取最近一次 <see cref="TryAllocate"/> 失败的原因；最近一次成功或尚未调用时为
    /// <see langword="null"/>。</para></summary>
    internal static string? LastFailure { get; private set; }

    /// <summary>
    /// Copies <paramref name="machineCode"/> into a fresh executable page and returns its address.
    /// <para>把 <paramref name="machineCode"/> 复制进新的可执行页并返回其地址。</para>
    /// </summary>
    /// <param name="machineCode">Position-independent machine code to publish.
    /// <para>待发布的与位置无关的机器码。</para></param>
    /// <param name="executable">Receives the executable address when the method returns <see langword="true"/>.
    /// <para>方法返回 <see langword="true"/> 时接收可执行地址。</para></param>
    /// <returns><see langword="true"/> when the page was published and is ready to execute.
    /// <para>页已发布并可以执行时为 <see langword="true"/>。</para></returns>
    internal static bool TryAllocate(ReadOnlySpan<byte> machineCode, out IntPtr executable)
    {
        executable = IntPtr.Zero;
        LastFailure = null;
        if (machineCode.IsEmpty || !OperatingSystem.IsLinux())
        {
            LastFailure = "unsupported-platform";
            return false;
        }

        try
        {
            var fileDescriptor = memfd_create("uotan-simd", 0);
            if (fileDescriptor < 0)
            {
                LastFailure = "memfd-create-errno-" + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                // Round the mapping up to one full page so speculative fetches past the blob tail stay inside the
                // mapping instead of walking off a size-limited file mapping.
                var length = (nuint)(((nuint)machineCode.Length + 0xFFF) & ~(nuint)0xFFF);
                if (ftruncate(fileDescriptor, (long)length) != 0)
                {
                    LastFailure = "ftruncate-errno-" + Marshal.GetLastWin32Error();
                    return false;
                }

                var writable = mmap(IntPtr.Zero, length, ProtectionReadWrite, MapShared, fileDescriptor, 0);
                if (writable == MapFailed)
                {
                    LastFailure = "mmap-rw-errno-" + Marshal.GetLastWin32Error();
                    return false;
                }

                fixed (byte* source = machineCode)
                {
                    Buffer.MemoryCopy(source, (void*)writable, machineCode.Length, machineCode.Length);
                }

                var executableMapping = mmap(IntPtr.Zero, length, ProtectionReadExecute, MapShared, fileDescriptor, 0);
                munmap(writable, length);
                if (executableMapping == MapFailed)
                {
                    LastFailure = "mmap-rx-errno-" + Marshal.GetLastWin32Error();
                    return false;
                }

                if (RuntimeInformation.ProcessArchitecture == Architecture.RiscV64)
                {
                    var start = executableMapping.ToInt64();
                    _ = syscall(RiscVFlushIcacheSyscallNumber, start, start + machineCode.Length, 0);
                }

                if (!Matches(machineCode, (byte*)executableMapping))
                {
                    LastFailure = "icache-stale";
                    munmap(executableMapping, length);
                    return false;
                }

                executable = executableMapping;
                return true;
            }
            finally
            {
                _ = close(fileDescriptor);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}