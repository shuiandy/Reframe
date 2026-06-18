namespace Reframe.Core;

/// <summary>
/// The geometry of one monitor (physical pixels). Lives in Core (not Services) so pure-logic Core code
/// — e.g. <see cref="LayoutKey"/> and the window-persistence engine — can consume monitor descriptions
/// without taking a Services dependency. The Win32 enumeration that produces these stays in
/// <c>Services.MonitorService</c> (it needs P/Invoke and is not unit-tested).
/// </summary>
public sealed record MonitorDesc(string DeviceName, bool IsPrimary,
    int X, int Y, int Width, int Height,            // rcMonitor
    int WorkX, int WorkY, int WorkW, int WorkH);    // rcWork
