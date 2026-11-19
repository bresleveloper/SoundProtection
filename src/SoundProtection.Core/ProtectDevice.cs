namespace SoundProtection.Core;

/// <summary>
/// The "Protect this device" button of the panel: when it is worth showing, and what one run of the installed script said.
/// The button goes by the output, not by the amber status: an output without the plug-in reads green while nothing plays, and
/// the button must still be there, or the person who plugged a new device in would never find it (<see cref="ShouldShow"/>).
/// <see cref="ResultMessage"/> turns the script's exit code into one plain sentence for the panel; any code other than 0 or 3
/// reads as the run failed. A declined administrator prompt and a missing script never produce a process, so the runner maps
/// both to exit code 1.
/// </summary>
public static class ProtectDevice
{
    private const string NotProtectedText =
        @"Not protected: Windows offers no hook on this device, or the run failed. See C:\Program Files\SoundProtection\install-log.txt.";

    /// <summary>
    /// True when the panel should show the button: the watched output is one the plug-in does not cover, and the protection
    /// switch is on. With the switch off the fix is the switch, not a run of the script.
    /// </summary>
    public static bool ShouldShow(ProtectionState state) =>
        !state.WatchedOutputCovered && state.Status is not ProtectionStatus.Off;

    /// <summary>One plain sentence about what a run of the script did, by its exit code (0 ok, 3 restart needed).</summary>
    public static string ResultMessage(int exitCode) => exitCode switch
    {
        0 => "Protected on this device.",
        3 => "Sound Protection is on, but Windows needs a restart of the PC before it runs. Restart, then try again.",
        _ => NotProtectedText,
    };
}
