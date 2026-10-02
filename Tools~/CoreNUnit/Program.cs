using System.Reflection;
using NUnitLite;
internal static class Program
{
    private static int Main(string[] args) { return new AutoRun(Assembly.GetExecutingAssembly()).Execute(args); }
}
