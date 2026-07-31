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

    private static LiveWindowRef L(int handle, WindowIdentity identity, string title = "")
        => new(new IntPtr(handle), identity, title);

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
        Assert.Empty(m.GetRestorable("desk"));          // ...just with nothing to act on yet

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
        Assert.Single(m.GetRestorable("desk"));                                   // only chrome is actionable
        Assert.Equal(IntPtr.Zero, m.EntriesFor("desk").Single(e => e.Identity == Notepad).Handle);
    }

    [Fact]
    public void ForgetWindow_only_drops_the_binding()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);

        m.ForgetWindow(new IntPtr(0x100));

        Assert.Equal(1, m.CountFor("desk"));
        Assert.Empty(m.GetRestorable("desk"));
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
        m.Capture("desk", new[]
        {
            W(0x100, Chrome, 100, 0, "Docs"),
            W(0x200, Chrome, 200, 0, "Mail"),
            W(0x300, Chrome, 300, 0, "News"),
        }, T0);
        m.PruneDead(_ => false);

        // New session, new handles (again out of order) — the captions say which is which.
        m.Reclaim("desk", new[] { L(0x920, Chrome, "News"), L(0x900, Chrome, "Docs"), L(0x910, Chrome, "Mail") });

        var plan = m.GetRestorePlan("desk", new[] { new IntPtr(0x900), new IntPtr(0x910), new IntPtr(0x920) });
        Assert.Equal(3, plan.Count);
        Assert.Equal(100, plan[0].Target.Left);
        Assert.Equal(200, plan[1].Target.Left);
        Assert.Equal(300, plan[2].Target.Left);
    }

    [Fact]
    public void Reclaim_binds_indistinguishable_windows_but_leaves_them_out_of_every_restore_plan()
    {
        // Same three windows with nothing to tell them apart. Reclaim still binds them — that is how records
        // stay bounded instead of piling up — but the ordinal order it used is arbitrary across a restart, so
        // acting on it would deal the three remembered rectangles out at random. Both restore entry points
        // therefore ignore them.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 100, 0), W(0x200, Chrome, 200, 0), W(0x300, Chrome, 300, 0) }, T0);
        m.PruneDead(_ => false);

        int claimed = m.Reclaim("desk", new[] { L(0x920, Chrome), L(0x900, Chrome), L(0x910, Chrome) });

        Assert.Equal(3, claimed);                                   // bound...
        Assert.Equal(3, m.CountFor("desk"));                        // ...with no duplicates
        Assert.All(m.EntriesFor("desk"), e => Assert.NotEqual(IntPtr.Zero, e.Handle));
        Assert.All(m.EntriesFor("desk"), e => Assert.False(e.ConfidentBinding));
        Assert.Empty(m.GetRestorable("desk"));                      // ...but nothing may be moved
        Assert.Empty(m.GetRestorePlan("desk", new[] { new IntPtr(0x900), new IntPtr(0x910), new IntPtr(0x920) }));
    }

    [Fact]
    public void Reclaim_is_a_no_op_for_an_unknown_key_or_when_nothing_matches()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false);

        Assert.Equal(0, m.Reclaim("stream", new[] { L(0x900, Chrome) }));  // no bucket
        Assert.Equal(0, m.Reclaim("desk", new[] { L(0x900, Notepad) }));   // different app
        Assert.Empty(m.GetRestorable("desk"));
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

    // ---- The restore path acts only on confident bindings ----
    //
    // These cover the *other* route to the same windows. Adoption fires from Capture; the display-change and
    // "Restore now" paths instead run Reclaim and then move whatever GetRestorable hands back. Gating only the
    // first left the identical bug alive on the second.

    private static WindowIdentity QQ => WindowIdentity.Create("qq", "Chrome_WidgetWin_1");

    private static CapturedWindow Q(int handle, int w, int h, string title)
        => new(new IntPtr(handle), QQ, title, new WindowRecord(0, 0, w, h, 0, 0, w, h, 1));

    [Fact]
    public void A_display_change_never_moves_windows_the_reclaim_could_not_identify()
    {
        // Reboot: the disk layout loads unbound. The monitor wakes and the settle path runs Reclaim before
        // restoring — which binds QQ's four same-class records to whatever QQ windows exist, by ordinal,
        // because nothing distinguishes them. That binding is a coin flip, so the restore must decline it
        // wholesale: this is the same reshaping the user photographed, reached through the topology-change
        // path instead of the adoption path.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 1015, 1015, "这是个什么群"),
            Q(0x200,  574, 2002, "QQ"),
            Q(0x300, 2220, 1487, "这是个什么群"),
            Q(0x400, 1921, 1152, "图片查看器"),
        }, T0);
        m.PruneDead(_ => false);   // reboot / app restart: every record unbound

        // The windows that came back all wear the duplicated caption, so none of them is identifiable.
        int claimed = m.Reclaim("desk", new[] { L(0x9100, QQ, "这是个什么群"), L(0x9200, QQ, "这是个什么群") });

        Assert.Equal(2, claimed);                        // bound, so no duplicate records accumulate
        Assert.Empty(m.GetRestorable("desk"));           // and not one window is moved
        Assert.Empty(m.GetRestorePlan("desk", new[] { new IntPtr(0x9100), new IntPtr(0x9200) }));
        Assert.Equal(4, m.CountFor("desk"));
    }

    [Fact]
    public void A_display_change_still_restores_everything_bound_in_this_session()
    {
        // THE regression that matters: the overwhelmingly common case is a display change with no restart at
        // all, where every record is still bound to its own HWND. Those bindings are certain, so the restore
        // must behave exactly as it always has — even for a maximally ambiguous app whose windows share one
        // class and one caption.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 1015, 1015, "这是个什么群"),
            Q(0x200,  574, 2002, "这是个什么群"),
            Q(0x300, 2220, 1487, "这是个什么群"),
            W(0x400, Chrome, 10, 20, "Inbox"),
        }, T0);

        // Nothing died; the monitors just changed. (PruneDead runs first on the real path, and keeps them.)
        m.PruneDead(_ => true);

        Assert.All(m.EntriesFor("desk"), e => Assert.True(e.ConfidentBinding));
        Assert.Equal(4, m.GetRestorable("desk").Count);
        var sizes = m.GetRestorable("desk").Select(p => p.Record.Right - p.Record.Left).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 574, 800, 1015, 2220 }, sizes); // 800 = the Chrome window's 10..810
    }

    [Fact]
    public void A_confidently_reclaimed_record_is_restored_after_a_restart()
    {
        // Both flavours of confidence survive into the restore path: a group-unique caption, and a forced 1:1.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 1015, 1015, "这是个什么群"),
            Q(0x200,  574, 2002, "QQ"),
            Q(0x300, 2220, 1487, "这是个什么群"),
            W(0x400, Chrome, 10, 20, "Inbox - Gmail"),
        }, T0);
        m.PruneDead(_ => false);

        m.Reclaim("desk", new[]
        {
            L(0x9100, QQ, "QQ"),                         // unique caption in its group
            L(0x9200, QQ, "这是个什么群"),                // ambiguous
            L(0x9300, QQ, "这是个什么群"),                // ambiguous
            L(0x9400, Chrome, "Reframe - GitHub"),       // caption changed, but it is the only Chrome window
        });

        var restorable = m.GetRestorable("desk");
        Assert.Equal(2, restorable.Count);
        Assert.Equal(574, restorable.Single(p => p.Handle == new IntPtr(0x9100)).Record.Right);
        Assert.Equal(10, restorable.Single(p => p.Handle == new IntPtr(0x9400)).Record.Left);
    }

    [Fact]
    public void Unbinding_clears_the_confidence_flag_so_it_can_never_go_stale()
    {
        // Confidence describes a binding, not a record. If it outlived the binding, the *next* claimant of
        // that record would inherit "we were sure" about a window it has nothing to do with.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);
        Assert.True(m.EntriesFor("desk")[0].ConfidentBinding);

        m.PruneDead(_ => false);
        Assert.False(m.EntriesFor("desk")[0].ConfidentBinding);
        Assert.Empty(m.GetRestorable("desk"));

        // Same for the explicit destroy notification.
        m.Reclaim("desk", new[] { L(0x900, Chrome, "GitHub") });
        Assert.True(m.EntriesFor("desk")[0].ConfidentBinding);
        m.ForgetWindow(new IntPtr(0x900));
        Assert.False(m.EntriesFor("desk")[0].ConfidentBinding);
        Assert.Empty(m.GetRestorable("desk"));
    }

    [Fact]
    public void The_confidence_flag_is_never_written_to_disk_and_comes_back_false()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);
        Assert.True(m.EntriesFor("desk")[0].ConfidentBinding);

        var after = new LayoutMemory();
        after.ImportFromDisk(m.ExportForDisk());

        // A binding is session-scoped, and so is any confidence in it: an imported record is unbound, so there
        // is nothing to have been sure about.
        Assert.False(after.EntriesFor("desk")[0].ConfidentBinding);
        Assert.Empty(after.GetRestorable("desk"));
        // ...but the layout still counts as "we know this display", or the settle path would never even try
        // to reclaim its windows.
        Assert.True(after.HasSnapshot("desk"));
        Assert.Equal(1, after.CountFor("desk"));
    }

    [Fact]
    public void HasSnapshot_ignores_bindings_entirely()
    {
        // The gate that decides whether a restore is attempted at all must not look at bindings: at the moment
        // it is asked, a just-loaded layout is entirely unbound, and Reclaim has not run yet.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20), W(0x200, Notepad, 30, 40) }, T0);

        m.PruneDead(_ => false); // nothing is bound, nothing is confident, nothing is restorable...

        Assert.Empty(m.GetRestorable("desk"));
        Assert.True(m.HasSnapshot("desk"));  // ...and yet we absolutely do know this display
    }
}
