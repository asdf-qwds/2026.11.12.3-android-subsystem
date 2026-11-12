using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AndroidSubsystem;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Combined => (StdOut + "\n" + StdErr).Trim();
}

internal static class ProcessRunner
{
    /// <summary>
    /// 运行外部程序（支持 .exe / .bat / .cmd）。沉默执行，返回退出码与输出。
    /// </summary>
    public static ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdIn = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        int timeoutMs = 0,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null)
    {
        bool isBatch = fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (isBatch)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(fileName);
        }
        else
        {
            psi.FileName = fileName;
        }

        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;

        if (environment != null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdout.AppendLine(e.Data);
            onStdOut?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderr.AppendLine(e.Data);
            onStdErr?.Invoke(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdIn != null)
        {
            try
            {
                proc.StandardInput.Write(stdIn);
                proc.StandardInput.Flush();
            }
            catch (IOException) { /* 子进程已退出 */ }
        }
        try { proc.StandardInput.Close(); } catch (IOException) { }

        if (timeoutMs > 0)
        {
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(true); } catch { }
                return new ProcessResult(-1, stdout.ToString(), stderr.ToString() + "\n(超时)");
            }
        }
        else
        {
            proc.WaitForExit();
        }

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// 在后台启动程序，不等待退出；输出写入日志文件。
    /// hideWindow=false 用于模拟器这类需要真正弹出窗口的 GUI 程序：
    /// 若带上 STARTF_USESHOWWINDOW=SW_HIDE 启动，进程会出现但主窗口被隐藏（任务栏有图标、界面不出来）。
    /// </summary>
    public static Process StartDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string logFile,
        string? workingDirectory = null,
        bool hideWindow = true)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = hideWindow,
            WindowStyle = hideWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.FileName = fileName;
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        if (environment != null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        var dir = Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stream = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stream) stream.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stream) stream.WriteLine("[stderr] " + e.Data); };
        proc.Exited += (_, _) => { lock (stream) { try { stream.Flush(); stream.Dispose(); } catch { } } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { proc.StandardInput.Close(); } catch (IOException) { }
        return proc;
    }
}
