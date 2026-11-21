using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AndroidSubsystem;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var settings = Settings.Load();

        var first = args
            .Select(a => a.Trim().TrimStart('-', '/').Trim().ToLowerInvariant())
            .FirstOrDefault(a => a.Length > 0);

        // -settings / -gui 以及“GUI 模式”下不带参数启动，都进图形界面
        if (first == "settings" || first == "setting" || first == "s")
            return RunGui(settings, settingsWindow: true);

        if (first == "gui" || first == "graphics" || first == "g")
            return RunGui(settings, settingsWindow: false);

        if (first == null && settings.GuiMode)
            return RunGui(settings, settingsWindow: false);

        return RunConsole(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunConsole(string[] args)
    {
        ConsoleUi.Banner();
        Updater.CleanupOld();

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

    private static int RunGui(Settings settings, bool settingsWindow)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "android_ui_error.txt"),
                        e.Exception.ToString());
                }
                catch { }
                MessageBox.Show(e.Exception.Message, "android", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            if (settingsWindow) Application.Run(new SettingsWindow(settings));
            else Application.Run(WebGuiWindow.ShowOrActivate(settings));
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("图形界面启动失败：" + ex.Message);
            return 1;
        }
    }
}


