using System.ComponentModel;
using System.Diagnostics;

namespace SoundProtection.App;

/// <summary>
/// Runs the installed script once, elevated, to attach the plug-in to every output device that can take it: the panel's
/// "Protect this device" button. <c>Repair</c> is journal-preserving and idempotent, so devices already covered stay as they
/// are and any new one is simply added; no device name needs to travel to the script. It waits on the administrator prompt
/// and on the run, which stops all sound for a few seconds, so it belongs off the UI thread (the panel runs it in Task.Run,
/// exactly like <see cref="TrayApp.RunInstaller"/> does). Returns the script's exit code; 1 when the run never happened:
/// the script is not there (nothing is installed on this machine), the person declined the administrator prompt, or the
/// wait ran out.
/// </summary>
internal static class ProtectDeviceRunner
{
    private const int UacDeclined = 1223; // ERROR_CANCELLED: the person closed the administrator prompt

    /// <summary>
    /// Runs <c>Repair -Endpoints All</c> and returns its exit code. One administrator Yes per run; the script falls back to
    /// its own installed copy of the plug-in, so no <c>-DllSource</c> is passed.
    /// </summary>
    public static int Run()
    {
        var path = ProtectionPaths.InstallerPath;
        if (!File.Exists(path))
        {
            return 1;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" -Action Repair -Endpoints All",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                return 1;
            }

            process.WaitForExit(240_000); // minutes of grace, not forever: the prompt, then a run that stops sound briefly
            return process.HasExited ? process.ExitCode : 1;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacDeclined)
        {
            return 1; // the person said no at the administrator prompt. Nothing was started.
        }
        catch (Exception ex)
        {
            Logging.Error("Could not run the installer for Protect this device", ex);
            return 1;
        }
    }
}
