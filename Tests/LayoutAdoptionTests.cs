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
    public void Multiple_restarted_windows_of_one_app_are_adopted_when_their_captions_identify_them()
    {
        // Two windows of one app, each wearing a caption unique in the group: that is enough to know which
        // record is which, so both go home — note the handles come back in the *opposite* order, and the
        // captions (not the handles) decide.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 100, 0, "Docs"), W(0x200, Chrome, 200, 0, "Mail") }, T0);
        m.PruneDead(_ => false);

        var adopted = m.Capture("desk", new[]
        {
            W(0x820, Chrome, 0, 0, "Docs"),
            W(0x810, Chrome, 0, 0, "Mail"),
        }, T0.AddMinutes(1));

        Assert.Equal(2, adopted.Count);
        Assert.Equal(100, adopted.Single(a => a.Handle == new IntPtr(0x820)).Target.Left); // "Docs"
        Assert.Equal(200, adopted.Single(a => a.Handle == new IntPtr(0x810)).Target.Left); // "Mail"
    }

    // ---- (6) Ambiguity: bind, but never move ----
    //
    // Identity is (process, class), and an Electron app puts everything in one class. When several records of
    // one identity are indistinguishable, an ordinal-order pairing is a coin flip — and a coin flip that
    // *moves windows*. So ambiguity binds (records must not pile up) and reports nothing.

    /// <summary>The exact shape observed on the machine that showed the bug — see the class remarks.</summary>
    private static CapturedWindow Q(int handle, int x, int y, int w, int h, string title)
        => new(new IntPtr(handle), QQ, title, new WindowRecord(x, y, x + w, y + h, x, y, x + w, y + h, 1));

    private static WindowIdentity QQ => WindowIdentity.Create("qq", "Chrome_WidgetWin_1");

    [Fact]
    public void Indistinguishable_windows_of_one_app_are_bound_and_recorded_but_never_moved()
    {
        // QQ NT: main panel, chat windows and the image viewer all live in chrome_widgetwin_1 with wildly
        // different sizes, so (process, class) cannot tell them apart. Two records share the caption
        // "这是个什么群"; the main panel restarted into the tray (minimized ⇒ never offered to capture) and the
        // image viewer wasn't reopened. Ordinal pairing used to hand a reopened chat window the main panel's
        // 574x2002 strip or the viewer's 1921x1152 box — photographed on real hardware.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 0, 0, 1015, 1015, "这是个什么群"),
            Q(0x200, 0, 0,  574, 2002, "QQ"),
            Q(0x300, 0, 0, 2220, 1487, "这是个什么群"),
            Q(0x400, 0, 0, 1921, 1152, "图片查看器"),
        }, T0);
        m.PruneDead(_ => false); // QQ restarts: every record is unbound

        var adopted = m.Capture("desk", new[]
        {
            Q(0x4400, 0, 0, 900, 900, "这是个什么群"),
            Q(0x8800, 0, 0, 900, 900, "这是个什么群"),
        }, T0.AddMinutes(1));

        Assert.Empty(adopted);                    // not one window is reshaped
        Assert.Equal(4, m.CountFor("desk"));      // ...yet no duplicate records pile up
        var bound = m.EntriesFor("desk").Where(e => e.Handle != IntPtr.Zero).ToList();
        Assert.Equal(2, bound.Count);
        // The two claimed records now describe the windows as they actually are (ordinary capture semantics).
        Assert.All(bound, e => Assert.Equal(900, e.Record.Right - e.Record.Left));
        // ...and the records nothing claimed keep their memory for a later, better-evidenced round.
        var untouched = m.EntriesFor("desk").Where(e => e.Handle == IntPtr.Zero).Select(e => e.Record.Right - e.Record.Left).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 1921, 2220 }, untouched);
    }

    [Fact]
    public void Within_an_ambiguous_group_a_uniquely_captioned_window_is_still_adopted()
    {
        // The other half of the same rule: ambiguity is per caption, not per app. QQ's main panel is the only
        // window called "QQ" on either side, so it — and only it — goes back to its 574x2002 strip.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 0, 0, 1015, 1015, "这是个什么群"),
            Q(0x200, 0, 0,  574, 2002, "QQ"),
            Q(0x300, 0, 0, 2220, 1487, "这是个什么群"),
        }, T0);
        m.PruneDead(_ => false);

        var adopted = m.Capture("desk", new[]
        {
            Q(0x4400, 0, 0, 900, 900, "QQ"),
            Q(0x5500, 0, 0, 900, 900, "这是个什么群"),
            Q(0x6600, 0, 0, 900, 900, "这是个什么群"),
        }, T0.AddMinutes(1));

        var one = Assert.Single(adopted);
        Assert.Equal(new IntPtr(0x4400), one.Handle);
        Assert.Equal(574, one.Target.Right - one.Target.Left);
        Assert.Equal(2002, one.Target.Bottom - one.Target.Top);
    }

    [Fact]
    public void Two_records_sharing_a_caption_adopt_nothing_even_one_at_a_time()
    {
        // Only one of the two same-captioned windows is back. It is still not knowable *which* one, and the
        // 1:1 rule must not paper over that: two unbound records is not one.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Untitled"), W(0x200, Chrome, 500, 600, "Untitled") }, T0);
        m.PruneDead(_ => false);

        var adopted = m.Capture("desk", new[] { W(0x900, Chrome, 0, 0, "Untitled") }, T0.AddMinutes(1));

        Assert.Empty(adopted);
        Assert.Equal(2, m.CountFor("desk"));
    }

    [Fact]
    public void A_single_window_app_is_still_adopted_when_its_caption_changed_completely()
    {
        // The original reported scenario, and the reason the 1:1 rule exists: one record, one window, and a
        // caption that follows the content (Chrome on another tab, charmap, an editor with another file).
        // There is nothing else either could be, so the window goes home despite matching no caption.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox - Gmail") }, T0);
        m.PruneDead(_ => false);

        var adopted = m.Capture("desk", new[] { W(0x900, Chrome, 0, 0, "Reframe - GitHub") }, T0.AddMinutes(1));

        var one = Assert.Single(adopted);
        Assert.Equal(new IntPtr(0x900), one.Handle);
        Assert.Equal(10, one.Target.Left);
    }

    [Fact]
    public void The_handle_fast_path_is_untouched_by_the_confidence_rules()
    {
        // Same session, nothing restarted, and a maximally ambiguous group: identical captions, several
        // windows, one identity. Every record keeps tracking its own handle exactly as before, nothing is
        // reported for adoption, and geometry follows the windows.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 0, 0, 1015, 1015, "这是个什么群"),
            Q(0x200, 0, 0,  574, 2002, "这是个什么群"),
            Q(0x300, 0, 0, 2220, 1487, "这是个什么群"),
        }, T0);

        var adopted = m.Capture("desk", new[]
        {
            Q(0x300, 30, 0, 2220, 1487, "这是个什么群"),
            Q(0x100, 10, 0, 1015, 1015, "这是个什么群"),
            Q(0x200, 20, 0,  574, 2002, "这是个什么群"),
        }, T0.AddSeconds(2));

        Assert.Empty(adopted);
        Assert.Equal(3, m.CountFor("desk"));
        foreach (var e in m.EntriesFor("desk"))
        {
            // Each record still holds its own window, and learned that window's new position.
            int width = e.Record.Right - e.Record.Left;
            int expectedLeft = width == 1015 ? 10 : width == 574 ? 20 : 30;
            Assert.Equal(expectedLeft, e.Record.Left);
        }
    }
}
