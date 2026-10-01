using BenchmarkDotNet.Running;

// BenchmarkSwitcher parses CLI flags (--filter, --job) only when args is passed through.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
