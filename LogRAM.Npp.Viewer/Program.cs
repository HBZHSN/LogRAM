using System.Globalization;
using System.Text;

namespace LogRAM.Npp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--self-test"]) return SelfTest.RunAsync().GetAwaiter().GetResult();
        if (args is ["--benchmark"]) return SelfTest.BenchmarkAsync().GetAwaiter().GetResult();
        if (args is ["--benchmark", var path]) return SelfTest.RealFileBenchmarkAsync(path).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        if (args is ["--ui-self-test"]) return SelfTest.RunUi();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        if (args.Length != 3 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent) ||
            !int.TryParse(args[1], out var owner) || parent == 0 || !Native.IsWindow(new IntPtr(parent))) return 1;
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
        using var window = new ViewerForm(new IntPtr(parent), owner, args[2]);
        try { Application.Run(window); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
