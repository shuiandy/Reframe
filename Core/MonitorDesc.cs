namespace Reframe.Core;

/// <summary>
/// The geometry of one monitor (physical pixels) plus the scale factor Windows is drawing it at. Lives in
/// Core (not Services) so pure-logic Core code — e.g. <see cref="LayoutKey"/> and the window-persistence
/// engine — can consume monitor descriptions without taking a Services dependency. The Win32 enumeration
/// that produces these stays in <c>Services.MonitorService</c> (it needs P/Invoke and is not unit-tested).
///
/// <para><b><paramref name="Dpi"/></b> is the monitor's <i>effective</i> DPI (<c>MDT_EFFECTIVE_DPI</c>):
/// 96 = 100%, 120 = 125%, 144 = 150%, 168 = 175%, 192 = 200%. An integer on purpose — it is compared for
/// equality and baked into a string key, and a float would make both fragile. It is the <b>last</b>
/// positional parameter and defaults to 96 so every existing construction site (and the geometry-only test
/// fixtures) keeps compiling and keeps meaning "unscaled", while callers that can measure it — only
/// <c>MonitorService</c> — pass the real value.</para>
/// </summary>
public sealed record MonitorDesc(string DeviceName, bool IsPrimary,
    int X, int Y, int Width, int Height,            // rcMonitor
    int WorkX, int WorkY, int WorkW, int WorkH,     // rcWork
    int Dpi = 96);                                  // effective DPI (96 = 100%); see the remarks above
