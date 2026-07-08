using Reframe.Services;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// ForensicProbe.IdleMsFrom: the pure idle-time computation used by the exit / engine-toggle forensic
/// markers. The only tricky part is the GetTickCount unsigned wraparound (~49.7 days of uptime), which
/// must yield a small positive delta rather than a ~4-billion bogus value. The Win32 glue
/// (GetLastInputInfo / GetForegroundWindow) is not exercised here.
/// </summary>
public class ForensicProbeTests
{
    [Fact(DisplayName = "Normal case: now - lastInput = idle")]
    public void Normal_Delta()
    {
        Assert.Equal(123456u, ForensicProbe.IdleMsFrom(200000u, 76544u));
    }

    [Fact(DisplayName = "Zero idle when no time has passed")]
    public void Zero_WhenEqual()
    {
        Assert.Equal(0u, ForensicProbe.IdleMsFrom(5000u, 5000u));
    }

    [Fact(DisplayName = "Wraparound: now wrapped past uint.MaxValue still gives a small delta")]
    public void Wraparound_SmallPositiveDelta()
    {
        // lastInput just before the counter wrapped; now is 100 ms after the wrap to 0.
        uint lastInput = uint.MaxValue - 99u; // 100 ms before overflow
        uint now = 0u;                          // wrapped
        // Elapsed is 100 ms (99 ticks up to MaxValue, +1 to reach 0, then... ) — unchecked subtraction
        // gives exactly 100.
        Assert.Equal(100u, ForensicProbe.IdleMsFrom(now, lastInput));
    }
}
