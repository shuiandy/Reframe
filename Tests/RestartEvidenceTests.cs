using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Adoption restore must be backed by evidence that the app <i>restarted</i>, not merely that a window with a
/// familiar caption appeared.
///
/// <para>The failure this pins down: the feature exists to undo the squeeze a display blackout inflicts on
/// windows. It was instead moving windows the user had just opened — close a terminal, open a new one, and the
/// new one is pulled to the old rectangle; the same for chat windows and dialogs — because the only evidence
/// the store had was a caption / a 1:1 leftover plus a 15-minute clock that re-arms on every close. The
/// process that owns the window is what tells the two apart: the same process still running means the user
/// opened a window (bind, track, never move); the process gone means the app came back (the clock decides).</para>
/// </summary>
public class RestartEvidenceTests
{
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private static WindowIdentity Terminal => WindowIdentity.Create("windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS");
    private static WindowIdentity Chrome => WindowIdentity.Create("chrome", "Chrome_WidgetWin_1");

    private static CapturedWindow W(int handle, WindowIdentity identity, int x, int y, uint pid, string title = "")
        => new(new IntPtr(handle), identity, title, new WindowRecord(x, y, x + 800, y + 600, x, y, x + 800, y + 600, 1), pid);

    /// <summary>A store whose "process gone" oracle is the given set of dead pids.</summary>
    private static LayoutMemory StoreWhereDead(params uint[] deadPids)
    {
        var dead = new HashSet<uint>(deadPids);
        return new LayoutMemory { ProcessGone = (pid, _) => dead.Contains(pid) };
    }

    // ---- The reported bug: the same live process opening another window ----

    [Fact]
    public void A_new_window_from_the_same_live_process_is_bound_and_tracked_but_never_moved()
    {
        var m = StoreWhereDead(/* nothing is dead: the terminal keeps running */);
        m.Capture("desk", new[] { W(0x100, Terminal, 10, 20, pid: 4000, "PowerShell") }, T0);
        m.PruneDead(_ => false, T0);                       // the user closes that window; the process lives on

        // Two minutes later they open a new terminal window, which the app places at (500, 500).
        var adopted = m.Capture("desk", new[] { W(0x200, Terminal, 500, 500, pid: 4000, "PowerShell") }, T0.AddMinutes(2));

        Assert.Empty(adopted);                             // nothing is moved...
        var e = Assert.Single(m.EntriesFor("desk"));
        Assert.Equal(new IntPtr(0x200), e.Handle);         // ...but the record claims the window (no duplicates)
        Assert.Equal(500, e.Record.Left);                  // and now simply records where the user has it
        Assert.False(e.ConfidentBinding);
    }

    [Fact]
    public void Closing_and_reopening_all_day_never_becomes_position_enforcement()
    {
        // The clock re-arms on every close; without process evidence this loop moved every single window.
        var m = StoreWhereDead();
        m.Capture("desk", new[] { W(0x100, Terminal, 10, 20, pid: 4000, "PowerShell") }, T0);
        var t = T0;
        for (int i = 1; i <= 5; i++)
        {
            m.PruneDead(_ => false, t);
            t = t.AddMinutes(3);
            var adopted = m.Capture("desk", new[] { W(0x100 + i, Terminal, 100 * i, 100 * i, pid: 4000, "PowerShell") }, t);
            Assert.Empty(adopted);
            Assert.Equal(100 * i, Assert.Single(m.EntriesFor("desk")).Record.Left);
        }
    }

    // ---- The case adoption exists for: the process actually went away ----

    [Fact]
    public void A_window_from_a_new_process_after_the_old_one_died_is_adopted_and_moved()
    {
        var m = StoreWhereDead(4000);                      // the old terminal process is gone
        m.Capture("desk", new[] { W(0x100, Terminal, 10, 20, pid: 4000, "PowerShell") }, T0);
        m.PruneDead(_ => false, T0);                       // crash / self-update / reboot

        var adopted = m.Capture("desk", new[] { W(0x200, Terminal, 0, 0, pid: 5000, "PowerShell") }, T0.AddMinutes(2));

        var one = Assert.Single(adopted);
        Assert.Equal(new IntPtr(0x200), one.Handle);
        Assert.Equal(10, one.Target.Left);                 // back to the remembered rectangle
        Assert.Equal(5000u, m.EntriesFor("desk")[0].ProcessId); // and the record now follows the new process
    }

    [Fact]
    public void The_clock_still_bounds_a_restart()
    {
        // Evidence of a restart does not resurrect a record whose window has been gone for hours.
        var m = StoreWhereDead(4000);
        m.Capture("desk", new[] { W(0x100, Terminal, 10, 20, pid: 4000, "PowerShell") }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { W(0x200, Terminal, 0, 0, pid: 5000, "PowerShell") }, T0.AddHours(3));

        Assert.Empty(adopted);
        Assert.Equal(0, m.EntriesFor("desk")[0].Record.Left);
    }

    [Fact]
    public void A_record_with_no_pid_is_never_adopted_once_process_evidence_is_available()
    {
        // A file written before the pid existed, or a failed lookup: no evidence either way ⇒ no move.
        var m = StoreWhereDead(4000, 5000, 6000);          // "everything is dead" would have said yes
        m.Capture("desk", new[] { W(0x100, Terminal, 10, 20, pid: 0, "PowerShell") }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { W(0x200, Terminal, 0, 0, pid: 5000, "PowerShell") }, T0.AddMinutes(1));

        Assert.Empty(adopted);
        Assert.Equal(5000u, m.EntriesFor("desk")[0].ProcessId); // the binding fills the gap for next time
    }

    [Fact]
    public void Without_a_process_oracle_the_clock_alone_decides_as_before()
    {
        // The pure default (no ProcessGone installed): behaviour of the earlier builds, which the older tests
        // rely on. The engine always installs an oracle.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Terminal, 10, 20, pid: 4000) }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { W(0x200, Terminal, 0, 0, pid: 4000) }, T0.AddMinutes(1));

        Assert.Single(adopted);
    }

    // ---- The display-change path (Reclaim + GetRestorable) obeys the same rule ----

    [Fact]
    public void Reclaim_does_not_license_a_move_for_a_window_of_the_same_live_process()
    {
        var m = StoreWhereDead();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, pid: 4000, "Inbox") }, T0);
        m.PruneDead(_ => false, T0);

        int claimed = m.Reclaim("desk", new[] { new LiveWindowRef(new IntPtr(0x200), Chrome, "Inbox", 4000) }, T0.AddMinutes(1));

        Assert.Equal(1, claimed);                          // bound, so records don't pile up
        Assert.Empty(m.GetRestorable("desk"));             // but not something a restore may act on
    }

    [Fact]
    public void Reclaim_licenses_a_move_when_the_process_restarted()
    {
        var m = StoreWhereDead(4000);
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, pid: 4000, "Inbox") }, T0);
        m.PruneDead(_ => false, T0);

        m.Reclaim("desk", new[] { new LiveWindowRef(new IntPtr(0x200), Chrome, "Inbox", 5000) }, T0.AddMinutes(1));

        var one = Assert.Single(m.GetRestorable("desk"));
        Assert.Equal(new IntPtr(0x200), one.Handle);
        Assert.Equal(10, one.Record.Left);
    }

    // ---- Pids survive the disk round trip: that is what makes "Reframe restarted, the apps didn't" a no-op ----

    [Fact]
    public void The_pid_is_persisted_and_restored()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Reframe.Tests." + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "window-layouts.json");
        try
        {
            var mem = new LayoutMemory();
            mem.Capture("desk", new[] { W(0x100, Chrome, 10, 20, pid: 4000, "Inbox") }, T0);
            Assert.True(LayoutStore.Save(mem.ExportForDisk(), file));

            var reloaded = StoreWhereDead();                // Chrome is still running under pid 4000
            reloaded.ImportFromDisk(LayoutStore.Load(file), T0.AddMinutes(1));
            Assert.Equal(4000u, reloaded.EntriesFor("desk")[0].ProcessId);

            // Reframe came back; Chrome never left, and the user moved it meanwhile. Not touched.
            var adopted = reloaded.Capture("desk", new[] { W(0x900, Chrome, 300, 300, pid: 4000, "Inbox") }, T0.AddMinutes(2));
            Assert.Empty(adopted);
            Assert.Equal(300, reloaded.EntriesFor("desk")[0].Record.Left);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void After_a_reboot_the_stored_pid_is_dead_and_the_app_is_put_back()
    {
        var mem = new LayoutMemory();
        mem.Capture("desk", new[] { W(0x100, Chrome, 10, 20, pid: 4000, "Inbox") }, T0);
        var file = mem.ExportForDisk();

        var reloaded = StoreWhereDead(4000);               // nothing from before the reboot is running
        reloaded.ImportFromDisk(file, T0.AddHours(8));
        var adopted = reloaded.Capture("desk", new[] { W(0x900, Chrome, 0, 0, pid: 7000, "Inbox") }, T0.AddHours(8).AddMinutes(1));

        var one = Assert.Single(adopted);
        Assert.Equal(10, one.Target.Left);
    }

    [Fact]
    public void An_old_file_without_pids_loads_with_pid_zero()
    {
        var m = new LayoutMemory();
        m.ImportFromDisk(new LayoutFile
        {
            Layouts = { new PersistedLayout { DisplayKey = "desk", Windows = { new PersistedWindow
                { Process = "chrome", Class = "chrome_widgetwin_1", Title = "Inbox", Left = 10, Top = 20,
                  Right = 810, Bottom = 620, ShowCmd = 1, LastSeenUtc = T0 } } } }
        }, T0);
        Assert.Equal(0u, m.EntriesFor("desk")[0].ProcessId);
    }

    // ---- The real oracle ----

    [Fact]
    public void Liveness_reports_the_current_process_as_running_and_a_nonexistent_pid_as_gone()
    {
        using var self = Process.GetCurrentProcess();
        uint pid = (uint)self.Id;
        string name = WindowIdentity.NormalizeProcess(self.ProcessName);

        Assert.False(ProcessLiveness.Probe(pid, name));
        Assert.False(ProcessLiveness.Probe(0, name));      // unknown is not evidence
        Assert.True(ProcessLiveness.Probe(pid, "definitely-not-this-image")); // a recycled pid reads as gone
        Assert.True(ProcessLiveness.Probe(3, name));       // pids are multiples of 4: no such process
    }

    [Fact]
    public void Liveness_memoizes_within_the_ttl_and_probes_again_after_it()
    {
        var now = T0;
        var l = new ProcessLiveness(() => now);
        using var self = Process.GetCurrentProcess();
        uint pid = (uint)self.Id;

        Assert.True(l.IsGone(pid, "definitely-not-this-image"));
        // Same pid, a name that would now say "alive" — but the cached answer stands inside the TTL.
        Assert.True(l.IsGone(pid, WindowIdentity.NormalizeProcess(self.ProcessName)));
        now = now.Add(ProcessLiveness.CacheTtl);
        Assert.False(l.IsGone(pid, WindowIdentity.NormalizeProcess(self.ProcessName)));
    }
}
