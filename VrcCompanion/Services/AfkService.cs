using System;
using System.Runtime.InteropServices;

namespace VrcCompanion.Services;

/// <summary>Idle time from Windows' last keyboard or mouse input.</summary>
public interface IAfkSensor
{
    TimeSpan GetIdleTime();
}

/// <summary>Local-only idle detection; it never reads input content.</summary>
public sealed class AfkSensor : IAfkSensor
{
    public TimeSpan GetIdleTime()
    {
        try
        {
            var info = new LastInputInfo
            {
                Size = (uint)Marshal.SizeOf<LastInputInfo>()
            };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var elapsed = unchecked((uint)GetTickCount64() - info.TickCount);
            return TimeSpan.FromMilliseconds(elapsed);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }
}
