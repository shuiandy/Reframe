using System;
using System.Collections.Generic;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// The layout memory once records stopped being keyed by HWND: a window whose process restarted (new handle)
/// must keep — and get back — its remembered geometry, and the store must stay bounded over time.
/// </summary>
public class LayoutMemoryIdentityTests
{
    private static readonly DateTime T0 = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

    private static WindowIdentity Chrome => WindowIdentity.Create("chrome", "Chrome_WidgetWin_1");
    private static WindowIdentity Notepad => WindowIdentity.Create("notepad", "Notepad");

    private static CapturedWindow W(int handle, WindowIdentity identity, int x, int y, string title = "")
        => new(new IntPtr(handle), identity, title, new WindowRecord(x, y, x + 800, y + 600, x, y, x + 800, y + 600, 1));

    private static LiveWindowRef L(int handle, WindowIdentity identity) => new(new IntPtr(handle), identity);

    [Fact]
    public void A_restarted_app_keeps_its_geometry_and_reclaims_it_with_a_new_handle()
    {
        // The exact bug: Chrome restarts at 12:33 with a brand-new HWND. Under the old handle-keyed store the
        // record was deleted by PruneDead and the window was never restored.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);

        m.PruneDead(_ => false);                 // old process exited: its handle is dead

        Assert.Equal(1, m.CountFor("desk"));     // the record survives...
        Assert.True(m.HasSnapshot("desk"));
        Assert.Empty(m.GetAll("desk"));          // ...just with nothing to act on yet

        int claimed = m.Reclaim("desk", new[] { L(0x777, Chrome) });

        Assert.Equal(1, claimed);
        var plan = m.GetRestorePlan("desk", new[] { new IntPtr(0x777) });
        Assert.Single(plan);
        Assert.Equal(10, plan[0].Target.Left);
        Assert.Equal(20, plan[0].Target.Top);
    }

    [Fact]
    public void PruneDead_unbinds_instead_of_deleting()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20), W(0x200, Notepad, 30, 40) }, T0);

        m.PruneDead(h => h != new IntPtr(0x200)); // notepad died

        Assert.Equal(2, m.CountFor("desk"));                               // both records still remembered
        Assert.Single(m.GetAll("desk"));                                   // only chrome is actionable
        Assert.Equal(IntPtr.Zero, m.EntriesFor("desk").Single(e => e.Identity == Notepad).Handle);
    }

    [Fact]
    public void ForgetWindow_only_drops_the_binding()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);

        m.ForgetWindow(new IntPtr(0x100));

        Assert.Equal(1, m.CountFor("desk"));
        Assert.Empty(m.GetAll("desk"));
        Assert.Equal(1, m.Reclaim("desk", new[] { L(0x999, Chrome) })); // still claimable later
    }

    [Fact]
    public void Capture_reclaims_an_orphaned_record_rather_than_piling_up_a_duplicate()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false);

        m.Capture("desk", new[] { W(0x500, Chrome, 55, 66) }, T0.AddMinutes(1));

        Assert.Equal(1, m.CountFor("desk")); // reclaimed, not duplicated
        var plan = m.GetRestorePlan("desk", new[] { new IntPtr(0x500) });
        Assert.Equal(new IntPtr(0x500), plan[0].Handle); // the record now points at the restarted window
    }

    [Fact]
    public void A_changed_window_title_does_not_disturb_matching()
    {
        // A browser caption follows the active tab; if the title were part of the identity this restart would
        // look like a different window and the record would be orphaned forever.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox - Gmail") }, T0);
        m.PruneDead(_ => false);

        m.Capture("desk", new[] { W(0x600, Chrome, 10, 20, "Reframe - GitHub") }, T0.AddMinutes(1));

        Assert.Equal(1, m.CountFor("desk"));
        Assert.Equal("Reframe - GitHub", m.EntriesFor("desk")[0].Title); // recorded, but not matched on
        Assert.Single(m.GetRestorePlan("desk", new[] { new IntPtr(0x600) }));
    }

    [Fact]
    public void Ordinals_are_handed_out_by_ascending_handle_and_stay_put()
    {
        var m = new LayoutMemory();
        // Deliberately out of handle order in the input list.
        m.Capture("desk", new[] { W(0x300, Chrome, 300, 0), W(0x100, Chrome, 100, 0), W(0x200, Chrome, 200, 0) }, T0);

        var byOrdinal = m.EntriesFor("desk").OrderBy(e => e.Ordinal).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, byOrdinal.Select(e => e.Ordinal));
        Assert.Equal(new[] { 100, 200, 300 }, byOrdinal.Select(e => e.Record.Left));

        // Re-capturing only the middle window must not renumber anything.
        m.Capture("desk", new[] { W(0x200, Chrome, 222, 0) }, T0.AddSeconds(2));
        var after = m.EntriesFor("desk").OrderBy(e => e.Ordinal).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, after.Select(e => e.Ordinal));
        Assert.Equal(new[] { 100, 222, 300 }, after.Select(e => e.Record.Left));
    }

    [Fact]
    public void Three_restarted_windows_of_one_app_land_on_three_different_records()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 100, 0), W(0x200, Chrome, 200, 0), W(0x300, Chrome, 300, 0) }, T0);
        m.PruneDead(_ => false);

        // New session, new handles (again out of order).
        m.Reclaim("desk", new[] { L(0x920, Chrome), L(0x900, Chrome), L(0x910, Chrome) });

        var plan = m.GetRestorePlan("desk", new[] { new IntPtr(0x900), new IntPtr(0x910), new IntPtr(0x920) });
        Assert.Equal(3, plan.Count);
        Assert.Equal(100, plan[0].Target.Left);
        Assert.Equal(200, plan[1].Target.Left);
        Assert.Equal(300, plan[2].Target.Left);
    }

    [Fact]
    public void Reclaim_is_a_no_op_for_an_unknown_key_or_when_nothing_matches()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false);

        Assert.Equal(0, m.Reclaim("stream", new[] { L(0x900, Chrome) }));  // no bucket
        Assert.Equal(0, m.Reclaim("desk", new[] { L(0x900, Notepad) }));   // different app
        Assert.Empty(m.GetAll("desk"));
    }

    [Fact]
    public void A_partial_identity_is_upgraded_once_the_process_name_resolves()
    {
        var half = WindowIdentity.Create("", "Chrome_WidgetWin_1"); // pid→name lookup failed this round
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, half, 10, 20) }, T0);
        Assert.False(m.EntriesFor("desk")[0].Identity.IsMatchable);

        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0.AddSeconds(2)); // same handle, full identity

        Assert.Equal(1, m.CountFor("desk"));
        Assert.Equal(Chrome, m.EntriesFor("desk")[0].Identity);

        // ...and it can now survive a restart.
        m.PruneDead(_ => false);
        Assert.Equal(1, m.Reclaim("desk", new[] { L(0x900, Chrome) }));
    }

    // ---- Bounding the store ----

    [Fact]
    public void Trim_forgets_stale_unbound_records_but_never_live_ones()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0.AddDays(-60)); // ancient
        m.Capture("desk", new[] { W(0x200, Notepad, 30, 40) }, T0.AddDays(-1));  // recent
        m.PruneDead(_ => false);                                                 // both windows gone
        m.Capture("desk", new[] { W(0x300, WindowIdentity.Create("code", "Chrome_WidgetWin_1"), 50, 60) }, T0.AddDays(-90));
        // (the last one is ancient by timestamp but currently bound to a live window)

        int dropped = m.Trim(maxPerKey: 100, maxAge: TimeSpan.FromDays(30), nowUtc: T0);

        Assert.Equal(1, dropped);
        var left = m.EntriesFor("desk").Select(e => e.Identity.ProcessName).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "code", "notepad" }, left); // ancient+unbound chrome gone; bound "code" kept
    }

    [Fact]
    public void Trim_caps_a_bucket_keeping_live_windows_then_the_most_recent()
    {
        var m = new LayoutMemory();
        for (int i = 0; i < 5; i++)
            m.Capture("desk", new[] { W(0x100 + i, WindowIdentity.Create("app" + i, "cls"), i, 0) }, T0.AddMinutes(-i));
        m.PruneDead(h => h == new IntPtr(0x104)); // only the oldest-stamped record still has a live window

        int dropped = m.Trim(maxPerKey: 2, maxAge: TimeSpan.FromDays(30), nowUtc: T0);

        Assert.Equal(3, dropped);
        var kept = m.EntriesFor("desk").Select(e => e.Identity.ProcessName).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Contains("app4", kept); // bound wins regardless of its older timestamp
        Assert.Contains("app0", kept); // then the most recently seen
    }

    [Fact]
    public void Trim_drops_buckets_that_end_up_empty()
    {
        var m = new LayoutMemory();
        m.Capture("stream", new[] { W(0x100, Chrome, 10, 20) }, T0.AddDays(-99));
        m.Capture("desk", new[] { W(0x200, Chrome, 10, 20) }, T0);
        m.PruneDead(h => h == new IntPtr(0x200));

        m.Trim(LayoutMemory.DefaultMaxPerKey, LayoutMemory.DefaultMaxAge, T0);

        Assert.Equal(new[] { "desk" }, m.Keys);
        Assert.False(m.HasSnapshot("stream"));
    }

    [Fact]
    public void Trim_leaves_a_healthy_store_alone()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20), W(0x200, Notepad, 30, 40) }, T0);

        Assert.Equal(0, m.Trim(LayoutMemory.DefaultMaxPerKey, LayoutMemory.DefaultMaxAge, T0.AddHours(1)));
        Assert.Equal(2, m.CountFor("desk"));
    }

    [Fact]
    public void Buckets_stay_isolated_across_display_keys_when_a_window_restarts()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.Capture("stream", new[] { W(0x100, Chrome, 900, 900) }, T0);
        m.PruneDead(_ => false);

        m.Reclaim("desk", new[] { L(0x800, Chrome) });

        Assert.Equal(10, m.GetRestorePlan("desk", new[] { new IntPtr(0x800) })[0].Target.Left);
        Assert.Empty(m.GetRestorePlan("stream", new[] { new IntPtr(0x800) })); // untouched: reclaim is per key
        Assert.Equal(900, m.EntriesFor("stream")[0].Record.Left);
    }
}
