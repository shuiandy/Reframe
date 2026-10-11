using System.Runtime.InteropServices;
using System.Text;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// Answers the one question the adoption restore hangs on: <i>is the process that owned this window still
/// running?</i> (see <see cref="RememberedWindow.ProcessId"/>). A pid alone is not enough — Windows recycles
/// them — so a pid that is alive but now belongs to a different image counts as gone, which is what makes the
/// post-reboot case work: the stored pids all belong to unrelated processes, or to nobody, and the apps coming
/// back up read as restarts.
///
/// <para><b>Biased towards "still running".</b> Every failure that is not a definite "no such process" — access
/// denied, an unreadable image name, an unexpected error — reports the process as alive, because "alive" is
/// the answer that moves nothing. A wrong "gone" yanks a window the user just opened; a wrong "alive" costs at
/// most one missed restore.</para>
///
/// <para>Cheap on purpose (<c>OpenProcess</c> with the limited query right, no process-table snapshot), and
/// memoized for a short while because the store asks per record per capture round. Owned and called by the
/// persistence worker thread only; not synchronized.</para>
/// </summary>
public sealed class ProcessLiveness
{
    /// <summary>How long one answer is trusted before the process is probed again.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private readonly Dictionary<uint, (bool Gone, DateTime At)> _cache = new();
    private readonly Func<DateTime> _clock;

    public ProcessLiveness() : this(() => DateTime.UtcNow) { }

    /// <summary>With an injectable clock (tests of the memoization).</summary>
    public ProcessLiveness(Func<DateTime> clock) => _clock = clock;

    /// <summary>
    /// True when no process with <paramref name="pid"/> is running, or the one that is does not match
    /// <paramref name="processName"/> (normalized as <see cref="WindowIdentity.NormalizeProcess"/> does — a
    /// recycled pid). Pid 0 is "unknown" and is reported as <b>not</b> gone: it is no evidence of anything.
    /// </summary>
    public bool IsGone(uint pid, string processName)
    {
        if (pid == 0) return false;
        var now = _clock();
        if (_cache.TryGetValue(pid, out var hit) && now - hit.At < CacheTtl) return hit.Gone;

        bool gone = Probe(pid, processName);
        _cache[pid] = (gone, now);
        if (_cache.Count > 512) Prune(now);
        return gone;
    }

    private void Prune(DateTime now)
    {
        var stale = new List<uint>();
        foreach (var (k, v) in _cache)
            if (now - v.At >= CacheTtl) stale.Add(k);
        foreach (var k in stale) _cache.Remove(k);
    }

    /// <summary>The uncached judgement. Never throws.</summary>
    public static bool Probe(uint pid, string processName)
    {
        if (pid == 0) return false; // unknown pid: OpenProcess would say "invalid parameter", which is not "gone"
        IntPtr h = IntPtr.Zero;
        try
        {
            h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                // No such process is the only failure we may read as "gone". Access denied (a protected
                // process) and everything else mean it exists and we simply cannot look at it.
                return Marshal.GetLastWin32Error() == NativeMethods.ERROR_INVALID_PARAMETER;
            }

            // The handle can be obtained for a process that has exited but is still held open somewhere.
            if (NativeMethods.GetExitCodeProcess(h, out uint code) && code != NativeMethods.STILL_ACTIVE)
                return true;

            // Alive — but is it the *same* process, or a recycled pid? Compare the image name; an unreadable
            // name is not evidence of a swap.
            if (string.IsNullOrEmpty(processName)) return false;
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            if (!NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size)) return false;
            string live = WindowIdentity.NormalizeProcess(sb.ToString(0, (int)size));
            return live.Length > 0 && !string.Equals(live, processName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (h != IntPtr.Zero) NativeMethods.CloseHandle(h);
        }
    }
}
