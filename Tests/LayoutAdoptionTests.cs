using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Adoption restore: what happens the moment a remembered record is claimed by a window it wasn't bound to.
///
/// <para>The direction matters more than anything else in this feature. When a record that was <b>unbound</b>
/// meets a live window, the window must be moved to the remembered place — <i>not</i> the memory rewritten to
/// wherever the app happened to reopen. Getting it backwards is silently fatal: Chrome restarts squashed, the
/// next 2 s capture overwrites the good geometry with the bad, and nothing ever restores it; and every disk
/// layout would be destroyed within one capture tick of startup, making persistence pointless.</para>
/// </summary>
public class LayoutAdoptionTests
{
    private static readonly DateTime T0 = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

    private static WindowIdentity Chrome => WindowIdentity.Create("chrome", "Chrome_WidgetWin_1");
    private static WindowIdentity Notepad => WindowIdentity.Create("notepad", "Notepad");

    private static CapturedWindow W(int handle, WindowIdentity identity, int x, int y, string title = "")
        => new(new IntPtr(handle), identity, title, new WindowRecord(x, y, x + 800, y + 600, x, y, x + 800, y + 600, 1));

    // ---- (1) A newly claimed record keeps its geometry and is reported for restore ----

    [Fact]
    public void A_newly_claimed_record_keeps_its_remembered_geometry_and_is_reported_for_restore()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);   // the good position
        m.PruneDead(_ => false);                                      // Chrome exits

        // Chrome restarts, squashed into the corner by whatever it remembered itself.
        var adopted = m.Capture("desk", new[] { W(0x500, Chrome, 0, 0) }, T0.AddMinutes(1));

        // The memory is untouched — and the caller is told to move the window there.
        Assert.Equal(10, m.EntriesFor("desk")[0].Record.Left);
        Assert.Equal(20, m.EntriesFor("desk")[0].Record.Top);
        var one = Assert.Single(adopted);
        Assert.Equal(new IntPtr(0x500), one.Handle);
        Assert.Equal(10, one.Target.Left);
        Assert.Equal(20, one.Target.Top);
    }

    [Fact]
    public void Adoption_still_refreshes_the_binding_title_and_timestamp()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);
        m.PruneDead(_ => false);

        m.Capture("desk", new[] { W(0x500, Chrome, 0, 0, "GitHub") }, T0.AddMinutes(1));

        var e = m.EntriesFor("desk")[0];
        Assert.Equal(new IntPtr(0x500), e.Handle);          // bound to the new window
        Assert.Equal("GitHub", e.Title);                    // caption refreshed (never used for matching)
        Assert.Equal(T0.AddMinutes(1), e.LastSeenUtc);      // and it won't age out
        Assert.Equal(10, e.Record.Left);                    // only the geometry is protected
    }

    [Fact]
    public void A_window_we_have_no_record_for_is_never_reported_as_an_adoption()
    {
        var m = new LayoutMemory();
        var adopted = m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        Assert.Empty(adopted); // brand-new record: there is nothing remembered to move it to
        Assert.Equal(10, m.EntriesFor("desk")[0].Record.Left);
    }

    [Fact]
    public void An_already_bound_record_keeps_tracking_the_window_as_before()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);

        // Same handle, user dragged the window: normal tracking, no adoption.
        var adopted = m.Capture("desk", new[] { W(0x100, Chrome, 300, 400) }, T0.AddSeconds(2));

        Assert.Empty(adopted);
        Assert.Equal(300, m.EntriesFor("desk")[0].Record.Left);
    }

    // ---- (3) One attempt per binding: no capture-by-capture tug of war ----

    [Fact]
    public void After_an_adoption_the_next_capture_resumes_normal_tracking()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false);

        var first = m.Capture("desk", new[] { W(0x500, Chrome, 0, 0) }, T0.AddMinutes(1));
        Assert.Single(first);

        // Next round: the record is bound, so it is reported once and only once — even if the window sprang
        // back to its own position, we record that rather than fight it every two seconds.
        var second = m.Capture("desk", new[] { W(0x500, Chrome, 0, 0) }, T0.AddMinutes(2));
        Assert.Empty(second);
        Assert.Equal(0, m.EntriesFor("desk")[0].Record.Left);

        // And a genuine later move is tracked normally.
        var third = m.Capture("desk", new[] { W(0x500, Chrome, 640, 480) }, T0.AddMinutes(3));
        Assert.Empty(third);
        Assert.Equal(640, m.EntriesFor("desk")[0].Record.Left);
    }

    [Fact]
    public void A_window_that_dies_again_gets_a_fresh_adoption_next_time()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false);
        Assert.Single(m.Capture("desk", new[] { W(0x500, Chrome, 0, 0) }, T0.AddMinutes(1)));

        m.PruneDead(_ => false); // restarted again
        var again = m.Capture("desk", new[] { W(0x900, Chrome, 55, 66) }, T0.AddMinutes(2));

        Assert.Single(again);
        Assert.Equal(10, again[0].Target.Left); // the good geometry was never overwritten by either restart
    }

    // ---- (2) "Capture now" is the opposite: the user is saying "remember it here" ----

    [Fact]
    public void Manual_capture_overwrites_a_reclaimed_record_and_reports_nothing()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false);

        var adopted = m.Capture("desk", new[] { W(0x500, Chrome, 55, 66) }, T0.AddMinutes(1), adoptGeometry: false);

        Assert.Empty(adopted);                                   // nothing gets moved as a side effect
        Assert.Equal(55, m.EntriesFor("desk")[0].Record.Left);   // current position wins: that was the request
        Assert.Equal(new IntPtr(0x500), m.EntriesFor("desk")[0].Handle);
    }

    [Fact]
    public void Manual_capture_leaves_untouched_records_alone()
    {
        // "Capture now" only overrides the windows it can actually see; a tray-hidden app keeps its memory.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20), W(0x200, Notepad, 30, 40) }, T0);
        m.PruneDead(h => h == new IntPtr(0x100));

        m.Capture("desk", new[] { W(0x100, Chrome, 99, 99) }, T0.AddMinutes(1), adoptGeometry: false);

        Assert.Equal(30, m.EntriesFor("desk").Single(e => e.Identity == Notepad).Record.Left);
    }

    // ---- (4) Windows the borderless engine owns are never adopted ----

    [Fact]
    public void An_engine_owned_window_is_not_claimed_and_not_reported()
    {
        // The engine's takeover set is filtered out before capture ever sees a window, so an engine-owned
        // window is simply absent from the candidate list: it can neither claim a record nor be moved by the
        // adoption path. (The engine additionally re-checks ownership at write time — see
        // PersistenceEngine.FilterWritable, shared with the display-change restore.)
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20), W(0x200, Notepad, 30, 40) }, T0);
        m.PruneDead(_ => false);

        // This round the engine owns the Chrome window, so only Notepad is offered.
        var adopted = m.Capture("desk", new[] { W(0x600, Notepad, 0, 0) }, T0.AddMinutes(1));

        Assert.Single(adopted);
        Assert.Equal(new IntPtr(0x600), adopted[0].Handle);
        Assert.DoesNotContain(adopted, a => a.Target.Left == 10);   // the Chrome record was never handed out
        Assert.Equal(IntPtr.Zero, m.EntriesFor("desk").Single(e => e.Identity == Chrome).Handle);
        Assert.Equal(10, m.EntriesFor("desk").Single(e => e.Identity == Chrome).Record.Left);
    }

    // ---- (5) A layout read off disk survives the first capture tick ----

    [Fact]
    public void A_disk_loaded_layout_is_adopted_not_destroyed_by_the_first_capture()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ReframeAdoptionTests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "window-layouts.json");
        try
        {
            var before = new LayoutMemory();
            before.Capture("desk", new[] { W(0x100, Chrome, 10, 20), W(0x200, Notepad, 300, 400) }, T0);
            Assert.True(LayoutStore.Save(before.ExportForDisk(), file));

            // New boot: everything is unbound, and both apps reopen wherever they feel like.
            var after = new LayoutMemory();
            after.ImportFromDisk(LayoutStore.Load(file));
            var adopted = after.Capture("desk", new[]
            {
                W(0x910, Chrome, 0, 0),
                W(0x920, Notepad, 5, 5),
            }, T0.AddDays(1));

            // The remembered layout is intact and both windows are queued to be moved onto it.
            Assert.Equal(2, adopted.Count);
            Assert.Equal(10, adopted.Single(a => a.Handle == new IntPtr(0x910)).Target.Left);
            Assert.Equal(300, adopted.Single(a => a.Handle == new IntPtr(0x920)).Target.Left);
            Assert.Equal(new[] { 10, 300 }, after.EntriesFor("desk").Select(e => e.Record.Left).OrderBy(x => x));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Multiple_restarted_windows_of_one_app_are_adopted_onto_their_own_records()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 100, 0), W(0x200, Chrome, 200, 0) }, T0);
        m.PruneDead(_ => false);

        var adopted = m.Capture("desk", new[] { W(0x820, Chrome, 0, 0), W(0x810, Chrome, 0, 0) }, T0.AddMinutes(1));

        Assert.Equal(2, adopted.Count);
        Assert.Equal(100, adopted.Single(a => a.Handle == new IntPtr(0x810)).Target.Left); // ordinal 0 ↔ lower handle
        Assert.Equal(200, adopted.Single(a => a.Handle == new IntPtr(0x820)).Target.Left);
    }
}
