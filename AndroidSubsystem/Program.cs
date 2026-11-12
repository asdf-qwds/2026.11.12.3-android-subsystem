using System;

namespace AndroidSubsystem;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        ConsoleUi.Banner();

        try
        {
            return await Cli.RunAsync(args);
        }
        catch (ArgumentException ex)
        {
            ConsoleUi.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("执行中断：" + ex.Message);
            if (Environment.GetEnvironmentVariable("ANDSYS_DEBUG") == "1")
                Console.WriteLine(ex.ToString());
            return 1;
        }
    }
}
