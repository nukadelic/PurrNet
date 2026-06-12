using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Mirrors every Unity log message of a play session into
/// <c>LOGS_DIR/unity_instance[_cloneN].txt</c> (default <c>S:\Projects\PurrNet\Logs</c>)
/// so host-migration runs across the main editor and ParrelSync clones can be compared
/// side by side with the PurrLay / PurrBalancer logs.
/// </summary>
public static class UnityInstanceFileLogger
{
    private static readonly object _gate = new object();
    private static StreamWriter _writer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init()
    {
        Shutdown(); // re-entry safety when domain reload is disabled

        var dir = Environment.GetEnvironmentVariable("LOGS_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = @"S:\Projects\PurrNet\Logs";

        try
        {
            Directory.CreateDirectory(dir);
            _writer = Open(dir, $"unity_instance{GetCloneSuffix()}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UnityInstanceFileLogger] Disabled: {e.Message}");
            return;
        }

        Application.logMessageReceivedThreaded += OnLog;
        Application.quitting += Shutdown;

        lock (_gate)
        {
            _writer.WriteLine(
                $"=== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} | pid {System.Diagnostics.Process.GetCurrentProcess().Id} | {Application.productName} ===");
        }
    }

    private static StreamWriter Open(string dir, string baseName)
    {
        try
        {
            return new StreamWriter(Path.Combine(dir, baseName + ".txt"), false, Encoding.UTF8) { AutoFlush = true };
        }
        catch (IOException)
        {
            // Another local instance holds the file (e.g. two standalone builds):
            // fall back to a pid-suffixed file instead of losing the logs.
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            return new StreamWriter(Path.Combine(dir, $"{baseName}_pid{pid}.txt"), false, Encoding.UTF8) { AutoFlush = true };
        }
    }

    private static string GetCloneSuffix()
    {
        try
        {
            // ParrelSync clones have a symlinked Assets folder; the clone index comes
            // from the project folder name ("MyProject_clone_0").
            var assets = new DirectoryInfo(Application.dataPath);
            if (!assets.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return "";

            var projectFolder = Path.GetFileName(Path.GetDirectoryName(Application.dataPath) ?? "");
            var match = Regex.Match(projectFolder, @"clone[_ ]?(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? $"_clone{match.Groups[1].Value}" : "_clone0";
        }
        catch
        {
            return "";
        }
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        var writer = _writer;
        if (writer == null)
            return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{type}] {condition}";
        if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) &&
            !string.IsNullOrEmpty(stackTrace))
        {
            line += Environment.NewLine + stackTrace.TrimEnd();
        }

        lock (_gate)
            writer.WriteLine(line);
    }

    private static void Shutdown()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        Application.quitting -= Shutdown;

        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
