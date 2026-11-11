using System.Runtime.InteropServices;

namespace SoundProtection.App;

/// <summary>Sleeps a few milliseconds with about 1 ms accuracy, using a high-resolution waitable timer.</summary>
internal static class HighResTimer
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    [ThreadStatic]
    private static IntPtr _timer;

    [ThreadStatic]
    private static bool _unavailable;

    public static void Sleep(int milliseconds)
    {
        if (_timer == IntPtr.Zero && !_unavailable)
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            _unavailable = _timer == IntPtr.Zero;
        }

        var due = -(long)milliseconds * 10_000L; // 100 ns units; negative means relative
        if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            WaitForSingleObject(_timer, 0xFFFFFFFF);
        }
        else
        {
            Thread.Sleep(milliseconds);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr completionArgument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
