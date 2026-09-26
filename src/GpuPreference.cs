using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Mine;

/// <summary>
/// On laptops with two GPUs, Windows runs OpenGL apps on the integrated one unless told otherwise.
/// This sets the per-app "High performance" graphics preference (the same one as Settings → System →
/// Display → Graphics) for the running executable. Windows reads it when a process starts, so the
/// first time it is set the game restarts itself once. On single-GPU machines it changes nothing.
/// </summary>
public static class GpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string HighPerformance = "GpuPreference=2;";
    private const string RestartedVariable = "MINE_GPU_RESTARTED";

    /// <summary>True when the game restarted itself and this process should just exit.</summary>
    public static bool EnsureHighPerformance(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return EnsureOnWindows(args);
        }
        catch (Exception)
        {
            return false; // no registry access: keep running on whatever GPU we got
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool EnsureOnWindows(string[] args)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key.GetValue(exe) as string == HighPerformance) return false;
        key.SetValue(exe, HighPerformance, RegistryValueKind.String);

        if (Environment.GetEnvironmentVariable(RestartedVariable) is not null) return false; // never loop
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment[RestartedVariable] = "1";
        Process.Start(start);
        return true;
    }
}
