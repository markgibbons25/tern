namespace Tern.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => TernApp.RunAsync(args);
}
