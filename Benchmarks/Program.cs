using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Anchors the assembly for the switcher above.</summary>
public partial class Program;
