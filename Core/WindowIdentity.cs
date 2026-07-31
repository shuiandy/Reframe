namespace Reframe.Core;

/// <summary>
/// A window's <b>cross-session</b> identity: what makes "the same window" recognizable after its process
/// restarted (new HWND) or after the machine rebooted (new everything). Deliberately made of two stable
/// parts only:
/// <list type="bullet">
/// <item><b>ProcessName</b> — normalized: no directory, no <c>.exe</c>, lower-cased
/// (<see cref="NormalizeProcess"/>), so <c>C:\...\Chrome.EXE</c>, <c>chrome.exe</c> and <c>chrome</c> all
/// collapse to <c>chrome</c>.</item>
/// <item><b>ClassName</b> — the Win32 window class (<c>GetClassName</c>), lower-cased. This is what
/// separates an app's real document window from its helper/popup windows of the same process (e.g. Chrome's
/// <c>chrome_widgetwin_1</c> browser frame vs. its other frames).</item>
/// </list>
///
/// <para><b>Why the title is NOT part of the identity.</b> A browser's caption follows the active tab, an
/// editor's follows the open file, a chat app's follows the unread count — folding it into equality would make
/// the identity drift on every tab switch and would (worse) look like a <i>different</i> window after a
/// restart, which is exactly the failure this type exists to fix. The title is recorded alongside the geometry
/// (<c>RememberedWindow.Title</c>) and <see cref="WindowMatcher"/> does use it — but only as tie-breaking
/// <i>evidence within</i> an identity group (an exact, group-unique caption match), never as part of the
/// identity itself. A caption that drifted simply yields no evidence, which costs a restore; it can never
/// orphan a record.</para>
///
/// <para><b>Identity alone is not enough to move a window.</b> Electron-style apps put unrelated windows in
/// one class (QQ NT: main panel, chat windows and image viewer are all <c>chrome_widgetwin_1</c>), so an
/// identity group routinely holds windows nothing here can distinguish. <see cref="WindowMatcher"/> therefore
/// separates "which record does this window get bound to" from "are we sure enough to move it".</para>
///
/// <para>Pure data, no Win32: the value is produced from strings the caller already has (the Win32 read lives
/// in <c>WindowScanner.ClassNameOf</c>), so identity extraction and matching are unit-testable standalone.</para>
/// </summary>
public sealed record WindowIdentity(string ProcessName, string ClassName)
{
    /// <summary>The "we couldn't tell" identity (both parts empty). Never matched across restarts — see <see cref="IsMatchable"/>.</summary>
    public static readonly WindowIdentity Unknown = new("", "");

    /// <summary>Build a normalized identity from a raw process name (or full exe path) and a raw class name.</summary>
    public static WindowIdentity Create(string? processName, string? className)
        => new(NormalizeProcess(processName), NormalizeClass(className));

    /// <summary>
    /// Normalize a process name: accept a full path or a bare name, drop the directory, drop a trailing
    /// <c>.exe</c>, trim, lower-case (invariant). Null/blank → empty string.
    /// </summary>
    public static string NormalizeProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        string s = processName.Trim();
        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Normalize a window class name: trim + lower-case (invariant). Win32 class registration is
    /// case-insensitive, so lower-casing both sides lets the record's default (ordinal) string equality do
    /// the right thing without a custom comparer.
    /// </summary>
    public static string NormalizeClass(string? className)
        => string.IsNullOrWhiteSpace(className) ? "" : className.Trim().ToLowerInvariant();

    /// <summary>Nothing usable was captured (both parts empty).</summary>
    public bool IsUnknown => ProcessName.Length == 0 && ClassName.Length == 0;

    /// <summary>
    /// Whether this identity may be used to re-claim a remembered record across sessions. Both parts must be
    /// present: a transiently failed process-name lookup (the pid→name resolution can return "") must not let
    /// two unrelated windows that share a generic class name adopt each other's geometry. When in doubt we
    /// restore less rather than restore wrongly — the next capture round retries with a complete identity.
    /// </summary>
    public bool IsMatchable => ProcessName.Length > 0 && ClassName.Length > 0;

    public override string ToString() => ProcessName + "!" + ClassName;
}
