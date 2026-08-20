using System.Collections.Generic;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

public class LayoutMemoryTests
{
    // Screen rect and normal (restore) rect are set equal here — the memory store doesn't care about the
    // distinction (that's WindowOps.RestorePlacement's concern), so the tests keep them simple.
    private static (IntPtr, WindowRecord) W(int handle, int x, int y, int w = 100, int h = 100, int showCmd = 1)
        => (new IntPtr(handle), new WindowRecord(x, y, x + w, y + h, x, y, x + w, y + h, showCmd));

    [Fact]
    public void Capture_then_restore_plan_returns_record_for_live_handle()
    {
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20), W(2, 30, 40) });

        var plan = m.GetRestorePlan("K", new[] { new IntPtr(1), new IntPtr(2) });

        Assert.Equal(2, plan.Count);
        var first = plan.First(p => p.Handle == new IntPtr(1)).Target;
        Assert.Equal(10, first.Left);
        Assert.Equal(20, first.Top);
    }

    [Fact]
    public void Restore_plan_omits_handles_without_a_record()
    {
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20) });

        // handle 2 is live but was never captured → not in the plan (we never invent a position).
        var plan = m.GetRestorePlan("K", new[] { new IntPtr(1), new IntPtr(2) });

        Assert.Single(plan);
        Assert.Equal(new IntPtr(1), plan[0].Handle);
    }

    [Fact]
    public void Restore_plan_omits_remembered_handles_not_currently_live()
    {
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20), W(2, 30, 40) });

        // Only handle 1 is alive now → handle 2 is dropped from the plan.
        var plan = m.GetRestorePlan("K", new[] { new IntPtr(1) });

        Assert.Single(plan);
        Assert.Equal(new IntPtr(1), plan[0].Handle);
    }

    [Fact]
    public void Different_display_keys_are_isolated_buckets()
    {
        var m = new LayoutMemory();
        m.Capture("desktop", new[] { W(1, 10, 20) });
        m.Capture("stream", new[] { W(1, 999, 999) });

        var desktop = m.GetRestorePlan("desktop", new[] { new IntPtr(1) });
        Assert.Equal(10, desktop[0].Target.Left);

        // The streaming layout must not have overwritten the desktop bucket.
        var stream = m.GetRestorePlan("stream", new[] { new IntPtr(1) });
        Assert.Equal(999, stream[0].Target.Left);
    }

    [Fact]
    public void Partial_capture_preserves_other_windows_in_the_bucket()
    {
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20), W(2, 30, 40) });
        // Re-capture only window 1 (e.g. window 2 was minimized this round and skipped).
        m.Capture("K", new[] { W(1, 11, 21) });

        var plan = m.GetRestorePlan("K", new[] { new IntPtr(1), new IntPtr(2) });
        Assert.Equal(2, plan.Count); // window 2 kept its earlier remembered position
        Assert.Equal(11, plan.First(p => p.Handle == new IntPtr(1)).Target.Left);
        Assert.Equal(30, plan.First(p => p.Handle == new IntPtr(2)).Target.Left);
    }

    [Fact]
    public void None_and_empty_keys_are_ignored_on_capture()
    {
        var m = new LayoutMemory();
        m.Capture(LayoutKey.None, new[] { W(1, 10, 20) });
        m.Capture("", new[] { W(1, 10, 20) });

        Assert.False(m.HasSnapshot(LayoutKey.None));
        Assert.False(m.HasSnapshot(""));
    }

    [Fact]
    public void HasSnapshot_reflects_capture()
    {
        var m = new LayoutMemory();
        Assert.False(m.HasSnapshot("K"));
        m.Capture("K", new[] { W(1, 10, 20) });
        Assert.True(m.HasSnapshot("K"));
    }

    [Fact]
    public void ForgetWindow_removes_handle_from_all_buckets()
    {
        var m = new LayoutMemory();
        m.Capture("a", new[] { W(1, 10, 20) });
        m.Capture("b", new[] { W(1, 30, 40) });

        m.ForgetWindow(new IntPtr(1));

        Assert.Empty(m.GetRestorePlan("a", new[] { new IntPtr(1) }));
        Assert.Empty(m.GetRestorePlan("b", new[] { new IntPtr(1) }));
    }

    [Fact]
    public void GetRestorable_returns_every_actionable_window_independent_of_a_live_set()
    {
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20), W(2, 30, 40) });

        // GetRestorable does not take a live-handle set — so a tray-hidden window (which a live scan would
        // miss) is still included.
        var all = m.GetRestorable("K");
        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.Handle == new IntPtr(1));
        Assert.Contains(all, p => p.Handle == new IntPtr(2));
        Assert.Empty(m.GetRestorable("other"));
    }

    [Fact]
    public void PruneDead_drops_only_dead_handles()
    {
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20), W(2, 30, 40) });

        // Pretend handle 2 is dead.
        m.PruneDead(h => h != new IntPtr(2));

        var plan = m.GetRestorePlan("K", new[] { new IntPtr(1), new IntPtr(2) });
        Assert.Single(plan);
        Assert.Equal(new IntPtr(1), plan[0].Handle);
    }

    // ---- Transient fallback keys are never written to ----

    [Theory]
    [InlineData("1024x768@0,0*")]
    [InlineData("1024x768@0,0#96*")]
    [InlineData("1024x768@0,0#120*")]
    [InlineData("800x600@0,0#96*")]
    public void Capture_refuses_to_create_a_bucket_for_a_transient_fallback_key(string transientKey)
    {
        // Second line of defence behind PersistenceEngine's own guard: the windows observed on Windows'
        // no-display 1024x768 desktop are all squeezed into it, so the bucket would be garbage — exactly the
        // three buckets found in the user's real window-layouts.json.
        var m = new LayoutMemory();
        var adopted = m.Capture(transientKey, new[] { W(1, 0, 0, 900, 700), W(2, 10, 10, 800, 600) });

        Assert.Empty(adopted);
        Assert.False(m.HasSnapshot(transientKey));
        Assert.Equal(0, m.CountFor(transientKey));
        Assert.Empty(m.Keys);
    }

    [Fact]
    public void Capture_still_writes_a_bucket_for_a_small_but_real_configuration()
    {
        // The other side of the narrow rule: a 1024x768 screen alongside the real panel is a configuration the
        // user arranged, and must keep being remembered.
        string realKey = LayoutKey.Compute(new[]
        {
            new MonitorDesc("\\\\.\\DISPLAY1", true, 0, 0, 7680, 2160, 0, 0, 7680, 2160, 168),
            new MonitorDesc("\\\\.\\DISPLAY2", false, 7680, 0, 1024, 768, 7680, 0, 1024, 768),
        });

        var m = new LayoutMemory();
        m.Capture(realKey, new[] { W(1, 10, 20) });

        Assert.True(m.HasSnapshot(realKey));
        Assert.Equal(new[] { realKey }, m.Keys);
    }

    [Fact]
    public void Import_keeps_transient_buckets_rather_than_destroying_data_on_sight()
    {
        // Deliberately NOT dropped at load. A single 1024x768 primary is almost always Windows' fallback, but
        // for a kiosk or VM console it is the real thing, and deleting their whole memory on first run would be
        // a far worse bug than the one being fixed. The behavioural gates make the bucket inert; aging removes
        // it. See LayoutKey.IsTransientKey.
        var m = new LayoutMemory();
        m.ImportFromDisk(new LayoutFile
        {
            Layouts = new List<PersistedLayout>
            {
                new() { DisplayKey = "1024x768@0,0#96*", Windows = { new PersistedWindow { Process = "potplayer" } } },
                new() { DisplayKey = "7680x2160@0,0#168*", Windows = { new PersistedWindow { Process = "potplayer" } } },
            }
        });

        Assert.Equal(2, m.Keys.Count);
        Assert.True(m.HasSnapshot("1024x768@0,0#96*"));
    }

    // ---- The adoption clock stops while the desktop is in a fallback mode ----

    private static readonly System.DateTime T = new(2026, 8, 19, 9, 0, 0, System.DateTimeKind.Utc);

    /// <summary>A store holding one unbound record, anchored (as a disk load would anchor it) at loadedAt.</summary>
    private static LayoutMemory LoadedAt(System.DateTime loadedAt, string key = "K")
    {
        var m = new LayoutMemory();
        m.ImportFromDisk(new LayoutFile
        {
            Layouts = new List<PersistedLayout>
            {
                new() { DisplayKey = key, Windows = { new PersistedWindow
                    { Process = "chrome", Class = "chrome_widgetwin_1", Title = "Inbox", Left = 10, Top = 20,
                      Right = 810, Bottom = 620, NormLeft = 10, NormTop = 20, NormRight = 810, NormBottom = 620,
                      ShowCmd = 1, LastSeenUtc = loadedAt } } },
            }
        }, loadedAt);
        return m;
    }

    [Fact]
    public void A_record_still_fresh_when_the_blackout_began_is_re_anchored_and_stays_adoptable()
    {
        // Reframe starts with the login while the TV is still on the console's input: the file loads and
        // anchors every record at that instant, but the desktop is in the 1024x768 fallback so nothing can be
        // adopted. Twenty minutes later the user switches back — past AdoptionWindow, yet not one second of
        // that was a chance the record was ever given.
        var m = LoadedAt(T);
        var entry = m.EntriesFor("K")[0];
        Assert.False(entry.IsAdoptionEligible(T.AddMinutes(20))); // would have expired...

        int shifted = m.ShiftUnboundAnchors(T, T.AddMinutes(20));

        Assert.Equal(1, shifted);
        Assert.True(entry.IsAdoptionEligible(T.AddMinutes(20))); // ...but the clock was stopped
        Assert.Equal(T.AddMinutes(20), entry.UnboundAtUtc);      // its age when the blackout began was zero
    }

    [Fact]
    public void A_record_already_stale_when_the_blackout_began_is_not_resurrected()
    {
        // The other half of the rule: a chat window closed days ago must not come back to life because a
        // monitor blinked. Its freshness at the start of the blackout is the fact we honour.
        var m = LoadedAt(T);
        var entry = m.EntriesFor("K")[0];
        var blackoutStart = T.AddHours(3); // long past AdoptionWindow already

        int shifted = m.ShiftUnboundAnchors(blackoutStart, blackoutStart.AddMinutes(20));

        Assert.Equal(0, shifted);
        Assert.Equal(T, entry.UnboundAtUtc); // untouched
        Assert.False(entry.IsAdoptionEligible(blackoutStart.AddMinutes(20)));
    }

    [Fact]
    public void Bound_records_are_untouched_by_the_paused_clock()
    {
        // A bound record has no anchor at all (the clock only runs while a record has no window) and the HWND
        // fast path that claims it is confident regardless of age.
        var m = new LayoutMemory();
        m.Capture("K", new[] { W(1, 10, 20) });
        var entry = m.EntriesFor("K")[0];
        Assert.Null(entry.UnboundAtUtc);

        Assert.Equal(0, m.ShiftUnboundAnchors(T, T.AddMinutes(20)));

        Assert.Null(entry.UnboundAtUtc);
        Assert.Equal(new IntPtr(1), entry.Handle);
    }

    [Fact]
    public void A_partial_age_is_preserved_across_the_blackout_rather_than_reset()
    {
        // Not "make everything fresh": a record that was already 10 minutes cold when the panel went away is
        // 10 minutes cold when it comes back — no more, no less.
        var m = LoadedAt(T);
        var entry = m.EntriesFor("K")[0];
        var blackoutStart = T.AddMinutes(10);
        var blackoutEnd = blackoutStart.AddMinutes(20);

        m.ShiftUnboundAnchors(blackoutStart, blackoutEnd);

        Assert.Equal(blackoutEnd - System.TimeSpan.FromMinutes(10), entry.UnboundAtUtc);
        Assert.True(entry.IsAdoptionEligible(blackoutEnd));                 // 10 min < the 15 min window
        Assert.False(entry.IsAdoptionEligible(blackoutEnd.AddMinutes(6)));  // and it keeps ticking afterwards
    }

    [Fact]
    public void A_zero_or_backwards_interval_changes_nothing()
    {
        var m = LoadedAt(T);
        var entry = m.EntriesFor("K")[0];

        Assert.Equal(0, m.ShiftUnboundAnchors(T, T));
        Assert.Equal(0, m.ShiftUnboundAnchors(T.AddMinutes(5), T));
        Assert.Equal(T, entry.UnboundAtUtc);
    }

    [Fact]
    public void Boot_inside_a_fallback_end_to_end_the_layout_is_still_restorable_20_minutes_later()
    {
        // The whole scenario in one test: reboot -> Reframe starts with the login -> the TV is on the console's
        // input so the desktop is in the fallback -> 20 minutes later the user switches back.
        var m = LoadedAt(T, "7680x2160@0,0#168*");
        var blackoutEnd = T.AddMinutes(20);

        // What the engine does on the way out of the episode, in order: stop the clock, then reclaim.
        m.ShiftUnboundAnchors(T, blackoutEnd);
        int claimed = m.Reclaim("7680x2160@0,0#168*", new[]
        {
            new LiveWindowRef(new IntPtr(0x9A0), WindowIdentity.Create("chrome", "chrome_widgetwin_1"), "Inbox")
        }, blackoutEnd);

        Assert.Equal(1, claimed);
        var restorable = m.GetRestorable("7680x2160@0,0#168*");
        Assert.Single(restorable);                      // confidently bound => actually moved
        Assert.Equal(10, restorable[0].Record.Left);    // to the remembered rectangle, not the squeezed one
    }

    [Fact]
    public void Without_the_paused_clock_the_same_boot_scenario_silently_moves_nothing()
    {
        // The regression guard: identical to the test above minus ShiftUnboundAnchors. The record is still
        // *bound* (that is what stops duplicates piling up) but not confidently, so nothing is moved — and the
        // next capture would then overwrite the good geometry with the squeezed one. This is exactly the
        // failure the paused clock exists to prevent; the two tests must keep telling different stories.
        var m = LoadedAt(T, "7680x2160@0,0#168*");

        int claimed = m.Reclaim("7680x2160@0,0#168*", new[]
        {
            new LiveWindowRef(new IntPtr(0x9A0), WindowIdentity.Create("chrome", "chrome_widgetwin_1"), "Inbox")
        }, T.AddMinutes(20));

        Assert.Equal(1, claimed);
        Assert.Empty(m.GetRestorable("7680x2160@0,0#168*"));
    }
}
