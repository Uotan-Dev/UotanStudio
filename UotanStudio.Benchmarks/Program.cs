using BenchmarkDotNet.Running;
using UotanStudio.Benchmarks.Performance;

if (args.Length > 0 && string.Equals(args[0], "--profile", StringComparison.Ordinal))
{
    return ProfileRunner.Run(args);
}

if (args.Length > 0 && string.Equals(args[0], "--image-run", StringComparison.Ordinal))
{
    return RealImageRunner.Run(args);
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;