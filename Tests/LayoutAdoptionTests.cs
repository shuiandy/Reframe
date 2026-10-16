using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reframe.Core;
using Reframe.Interop;
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
///
/// <para><b>And it is time-limited</b> (<see cref="LayoutMemory.AdoptionWindow"/>). "The app is coming back"
/// only reads as true for a few minutes; after that the same claim is just a record enforcing a position on a
/// window the user opened themselves. Every test here therefore drives <c>PruneDead</c> with an explicit
/// clock: the gap between a record losing its window and a window claiming it is now load-bearing, so it is
/// never left to wall time.</para>
///
/// <para><b>And it only repairs a squeeze</b> (<see cref="AdoptionGate"/>). Even a fresh, confident claim moves
/// nothing unless the window sits inside the 1024×768 display fallback box while its record does not — the one
/// shape Windows itself produces when the only display goes away. A restarted window that comes back where its
/// app put it is bound and tracked from there. The fixtures make the two shapes explicit: <c>W</c> is a normal
/// 1600×1000 window, <c>S</c> the same window squeezed to 800×600 near the origin.</para>
/// </summary>
public class LayoutAdoptionTests
{
    private static readonly DateTime T0 = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

    private static WindowIdentity Chrome => WindowIdentity.Create("chrome", "Chrome_WidgetWin_1");
    private static WindowIdentity Notepad => WindowIdentity.Create("notepad", "Notepad");

    /// <summary>A normal-sized window on a big desktop: 1600×1000 at (x, y). Never fits the display fallback box.</summary>
    private static CapturedWindow W(int handle, WindowIdentity identity, int x, int y, string title = "")
        => new(new IntPtr(handle), identity, title, new WindowRecord(x, y, x + 1600, y + 1000, x, y, x + 1600, y + 1000, 1));

    /// <summary>
    /// The same window as it comes back <i>squeezed</i>: 800×600 near the origin, i.e. inside the 1024×768 box
    /// Windows collapses the desktop to while the only display is away. This is the shape adoption exists to
    /// repair (<see cref="AdoptionGate"/>); a restarted window that is not squeezed is bound and tracked, never moved.
    /// </summary>
    private static CapturedWindow S(int handle, WindowIdentity identity, int x, int y, string title = "")
        => new(new IntPtr(handle), identity, title, new WindowRecord(x, y, x + 800, y + 600, x, y, x + 800, y + 600, 1));

    // ---- (1) A newly claimed record keeps its geometry and is reported for restore ----

    [Fact]
    public void A_newly_claimed_record_keeps_its_remembered_geometry_and_is_reported_for_restore()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);   // the good position
        m.PruneDead(_ => false, T0);                                  // Chrome exits

        // Chrome comes back squashed into the fallback corner (closed and reopened while the display was away).
        var adopted = m.Capture("desk", new[] { S(0x500, Chrome, 0, 0) }, T0.AddMinutes(1));

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
        m.PruneDead(_ => false, T0);

        m.Capture("desk", new[] { S(0x500, Chrome, 0, 0, "GitHub") }, T0.AddMinutes(1));

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
        m.PruneDead(_ => false, T0);

        var first = m.Capture("desk", new[] { S(0x500, Chrome, 0, 0) }, T0.AddMinutes(1));
        Assert.Single(first);

        // Next round: the record is bound, so it is reported once and only once — even if the window sprang
        // back to its own position, we record that rather than fight it every two seconds.
        var second = m.Capture("desk", new[] { S(0x500, Chrome, 0, 0) }, T0.AddMinutes(2));
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
        m.PruneDead(_ => false, T0);
        Assert.Single(m.Capture("desk", new[] { S(0x500, Chrome, 0, 0) }, T0.AddMinutes(1)));

        m.PruneDead(_ => false, T0.AddMinutes(1)); // restarted again — and unbound afresh, so the clock restarts
        var again = m.Capture("desk", new[] { S(0x900, Chrome, 55, 66) }, T0.AddMinutes(2));

        Assert.Single(again);
        Assert.Equal(10, again[0].Target.Left); // the good geometry was never overwritten by either restart
    }

    // ---- (2) "Capture now" is the opposite: the user is saying "remember it here" ----

    [Fact]
    public void Manual_capture_overwrites_a_reclaimed_record_and_reports_nothing()
    {
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20) }, T0);
        m.PruneDead(_ => false, T0);

        // Squeezed or not (this one is), "Capture now" records the window as it stands.
        var adopted = m.Capture("desk", new[] { S(0x500, Chrome, 55, 66) }, T0.AddMinutes(1), adoptGeometry: false);

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
        m.PruneDead(h => h == new IntPtr(0x100), T0);

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
        m.PruneDead(_ => false, T0);

        // This round the engine owns the Chrome window, so only Notepad is offered.
        var adopted = m.Capture("desk", new[] { S(0x600, Notepad, 0, 0) }, T0.AddMinutes(1));

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

            // New boot a day later with the TV on another input: everything is unbound, and both apps come up
            // squeezed into the fallback box. The record timestamps are yesterday's, but the adoption clock is
            // anchored to the *load*, so the two minutes it takes the apps to come up is what counts.
            var boot = T0.AddDays(1);
            var after = new LayoutMemory();
            after.ImportFromDisk(LayoutStore.Load(file), boot);
            var adopted = after.Capture("desk", new[]
            {
                S(0x910, Chrome, 0, 0),
                S(0x920, Notepad, 5, 5),
            }, boot.AddMinutes(2));

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
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[]
        {
            S(0x820, Chrome, 0, 0, "Docs"),
            S(0x810, Chrome, 0, 0, "Mail"),
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
        m.PruneDead(_ => false, T0); // QQ restarts: every record is unbound

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
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[]
        {
            Q(0x4400, 0, 0, 900, 700, "QQ"),            // all three squeezed into the fallback box
            Q(0x5500, 0, 0, 900, 700, "这是个什么群"),
            Q(0x6600, 0, 0, 900, 700, "这是个什么群"),
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
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { S(0x900, Chrome, 0, 0, "Untitled") }, T0.AddMinutes(1)); // squeezed, even

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
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { S(0x900, Chrome, 0, 0, "Reframe - GitHub") }, T0.AddMinutes(1));

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

    // ---- (7) Freshness: adoption is for windows that came *back*, not for windows that look familiar ----
    //
    // Knowing *which* window a record describes is necessary but not sufficient. Adoption restore was built
    // for the seconds-to-minutes gap in which an app returns — a crash, a self-update, an installer, the
    // desktop repopulating after a reboot. Untimed, the very same rules become permanent position
    // enforcement: reopen a chat window whose caption is unique in its group and it is dragged to a rectangle
    // remembered days ago, every single time, with nothing in the UI to explain it. LayoutMemory.AdoptionWindow
    // is where that stops. Past it a record is still bound (records must not pile up) and still tracks its
    // window — it just never asks for it to be moved.

    /// <summary>The reported defect, end to end, in the store that produced it.</summary>
    [Fact]
    public void A_days_old_record_never_hijacks_a_freshly_reopened_window_of_the_same_name()
    {
        // Real hardware: QQ NT keeps its main panel, every chat window and the image viewer in one class, so
        // only the caption tells them apart — and "这是个什么群" is unique among the records in this bucket.
        // The user closed that group chat on Monday; on Thursday they reopen it and QQ sizes and places it
        // perfectly well by itself. The caption rule still fires (it always will), so the record is claimed —
        // but three days is nobody's idea of "the app is restarting", and the window must be left alone.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 0, 0, 1015, 1015, "这是个什么群"),
            Q(0x200, 0, 0,  574, 2002, "QQ"),
            Q(0x300, 0, 0, 1921, 1152, "图片查看器"),
        }, T0);
        m.PruneDead(h => h != new IntPtr(0x100), T0);      // just that chat window closed

        // (Squeezed on purpose, so the clock is the only thing standing between this window and a move.)
        var reopened = T0.AddDays(3);
        var adopted = m.Capture("desk", new[] { Q(0x9900, 0, 0, 900, 700, "这是个什么群") }, reopened);

        Assert.Empty(adopted);                              // nothing is moved: the whole point
        Assert.Equal(3, m.CountFor("desk"));                // and no duplicate record piles up
        var chat = m.EntriesFor("desk").Single(e => e.Handle == new IntPtr(0x9900));
        Assert.False(chat.ConfidentBinding);                // so no restore path will move it either
        Assert.Equal(0, chat.Record.Left);                  // the record learned where QQ actually put it
        Assert.Equal(900, chat.Record.Right - chat.Record.Left);
    }

    [Fact]
    public void A_crash_restart_inside_the_adoption_window_is_still_adopted_by_a_unique_caption()
    {
        // The other side of the same rule, through the same code path: the app died and was relaunched a
        // minute later (a crash, a self-update, an installer). That is exactly what adoption is for.
        var m = new LayoutMemory();
        m.Capture("desk", new[]
        {
            Q(0x100, 0, 0, 1015, 1015, "这是个什么群"),
            Q(0x200, 0, 0,  574, 2002, "QQ"),
            Q(0x300, 0, 0, 1921, 1152, "图片查看器"),
        }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { Q(0x9900, 0, 0, 900, 700, "这是个什么群") }, T0.AddMinutes(1)); // squeezed

        var one = Assert.Single(adopted);
        Assert.Equal(new IntPtr(0x9900), one.Handle);
        Assert.Equal(1015, one.Target.Right - one.Target.Left);   // back to its remembered shape
        Assert.Equal(1015, m.EntriesFor("desk").Single(e => e.Handle == new IntPtr(0x9900)).Record.Right);
    }

    [Fact]
    public void The_same_restart_sixteen_minutes_later_is_bound_and_tracked_instead_of_moved()
    {
        // Identical inputs to the test above except for the clock. Fifteen minutes is the whole difference
        // between "it came back" and "the user opened it".
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox - Gmail") }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { S(0x900, Chrome, 24, 24, "Inbox - Gmail") }, T0.AddMinutes(16)); // squeezed, even

        Assert.Empty(adopted);
        var e = m.EntriesFor("desk")[0];
        Assert.Equal(new IntPtr(0x900), e.Handle);   // bound...
        Assert.False(e.ConfidentBinding);            // ...but not on evidence anything may act on
        Assert.Equal(24, e.Record.Left);             // ordinary capture: the record takes the window's shape
    }

    [Theory]
    [InlineData(0, true)]      // the instant it died
    [InlineData(14, true)]
    [InlineData(15, true)]     // exactly the window: inclusive
    [InlineData(16, false)]
    [InlineData(60, false)]
    public void The_adoption_window_boundary_is_inclusive_to_the_minute(int minutesLater, bool adopted)
    {
        // The boundary is defined as `age <= AdoptionWindow`, i.e. a record unbound exactly AdoptionWindow ago
        // is still adoptable. (Nothing hinges on which side is chosen — everything hinges on it being pinned.)
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);
        m.PruneDead(_ => false, T0);

        var result = m.Capture("desk", new[] { S(0x900, Chrome, 0, 0, "Inbox") }, T0.AddMinutes(minutesLater));

        Assert.Equal(adopted, result.Count == 1);
        Assert.Equal(adopted ? 10 : 0, m.EntriesFor("desk")[0].Record.Left);
    }

    [Fact]
    public void A_disk_loaded_record_is_only_tracked_when_the_app_starts_long_after_the_load()
    {
        // The reboot case at its far end: the layout file is the same, but this app was opened by hand in the
        // afternoon rather than started with the session. Anchoring every imported record to the load is what
        // makes both halves work — see the sibling test above for the app that comes up two minutes in.
        var before = new LayoutMemory();
        before.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);

        var boot = T0.AddDays(1);
        var after = new LayoutMemory();
        after.ImportFromDisk(before.ExportForDisk(), boot);

        var adopted = after.Capture("desk", new[] { S(0x910, Chrome, 0, 0, "Inbox") }, boot.AddMinutes(16)); // squeezed, even

        Assert.Empty(adopted);
        Assert.Equal(0, after.EntriesFor("desk")[0].Record.Left);     // left where it opened: the clock ran out
        Assert.False(after.EntriesFor("desk")[0].ConfidentBinding);
    }

    [Fact]
    public void A_window_that_dies_after_a_stale_binding_is_fresh_again_when_it_comes_back()
    {
        // Freshness is a property of the *current* absence, not a mark the record wears forever. A record that
        // went stale, was re-bound to a window the user opened, and then lost that window has a brand-new
        // "unbound at" — so the ordinary crash-restart case works again immediately, and it restores the
        // geometry the record has been tracking since, not the ancient one.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);
        m.PruneDead(_ => false, T0);

        // Hours later the user opens it again: bound, tracked, not moved.
        var late = T0.AddHours(5);
        Assert.Empty(m.Capture("desk", new[] { W(0x900, Chrome, 640, 480, "Inbox") }, late));
        Assert.Equal(640, m.EntriesFor("desk")[0].Record.Left);

        // Now *that* window crashes and is back within the window.
        m.PruneDead(_ => false, late.AddMinutes(1));
        var adopted = m.Capture("desk", new[] { S(0xA00, Chrome, 0, 0, "Inbox") }, late.AddMinutes(2)); // squeezed this time

        var one = Assert.Single(adopted);
        Assert.Equal(new IntPtr(0xA00), one.Handle);
        Assert.Equal(640, one.Target.Left);   // the position it had been tracking, not the T0 one
    }

    [Fact]
    public void A_window_open_all_week_is_untouched_by_the_freshness_rule()
    {
        // THE regression to guard: a record that never lost its window is re-claimed by the HWND fast path,
        // which is not cross-session re-identification at all and has no business expiring. Days of ordinary
        // capture ticks must keep tracking it, and it must stay restorable throughout.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 10, 20, "Inbox") }, T0);

        for (int day = 1; day <= 5; day++)
        {
            m.PruneDead(_ => true, T0.AddDays(day));   // still alive: nothing is unbound, nothing is stamped
            Assert.Empty(m.Capture("desk", new[] { W(0x100, Chrome, 10 * day, 20, "Inbox") }, T0.AddDays(day)));
        }

        var e = m.EntriesFor("desk")[0];
        Assert.Equal(new IntPtr(0x100), e.Handle);
        Assert.True(e.ConfidentBinding);                  // a display change five days in still restores it
        Assert.Null(e.UnboundAtUtc);
        Assert.Equal(50, e.Record.Left);
        Assert.Single(m.GetRestorable("desk"));
    }

    // ---- (8) The squeeze gate: adoption repairs a display fallback, it does not place windows ----
    //
    // Identity says which window a record describes; freshness says the record's window went away recently.
    // Neither says the new window is in the wrong place, and usually it is not: the user closed one window and
    // opened another, or an app started with the session, and the app put the window where it wanted it. The
    // one thing worth undoing is the squeeze Windows applies while the only display is away — every window
    // crammed into the 1024x768 box at the origin. So a claim is only acted on when the window looks like that
    // and its remembered rectangle does not. (AdoptionGate; see AdoptionGateTests for the rectangle rules.)

    [Fact]
    public void A_restarted_window_that_comes_back_where_its_app_put_it_is_tracked_not_moved()
    {
        // The reported defect: close a browser window, open another one a minute later (same process and
        // class, 1:1 — a confident, fresh claim) and the new window used to be yanked to the old rectangle.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 2400, 300) }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { W(0x900, Chrome, 1200, 500) }, T0.AddMinutes(1));

        Assert.Empty(adopted);                                   // nothing is moved
        var e = m.EntriesFor("desk")[0];
        Assert.Equal(new IntPtr(0x900), e.Handle);               // the record is claimed (no duplicate)
        Assert.Equal(1, m.CountFor("desk"));
        Assert.Equal(1200, e.Record.Left);                       // and now describes the window as it is
        Assert.Equal(500, e.Record.Top);
    }

    [Fact]
    public void A_tracked_restart_is_still_restored_to_its_own_place_on_the_next_display_change()
    {
        // Declining the move must not demote the record: the matcher was sure this is the window, the record
        // now holds that window's own geometry, so a display change puts it back *there* — not at the T0 rect.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 2400, 300) }, T0);
        m.PruneDead(_ => false, T0);
        Assert.Empty(m.Capture("desk", new[] { W(0x900, Chrome, 1200, 500) }, T0.AddMinutes(1)));

        var e = m.EntriesFor("desk")[0];
        Assert.True(e.ConfidentBinding);
        var one = Assert.Single(m.GetRestorable("desk"));
        Assert.Equal(new IntPtr(0x900), one.Handle);
        Assert.Equal(1200, one.Record.Left);
    }

    [Fact]
    public void Apps_that_start_with_the_session_on_a_healthy_display_are_left_where_they_open()
    {
        // The login case. Reframe starts with the session, the layout file loads unbound, and the user's apps
        // come up over the next minutes — on a display that is perfectly fine, each where it remembers being.
        // Every one of them is a fresh 1:1 claim, and not one of them is to be moved: the apps know their own
        // positions, and only a squeeze (the disk-loaded test in section 5) justifies overriding that.
        var before = new LayoutMemory();
        before.Capture("desk", new[] { W(0x100, Chrome, 2400, 300, "Inbox"), W(0x200, Notepad, 100, 1200) }, T0);

        var boot = T0.AddDays(1);
        var after = new LayoutMemory();
        after.ImportFromDisk(before.ExportForDisk(), boot);

        var adopted = after.Capture("desk", new[]
        {
            W(0x910, Chrome, 2500, 320, "Inbox"),   // roughly where it was — Chrome remembered that itself
            W(0x920, Notepad, 1800, 200),           // somewhere else entirely — also Notepad's own choice
        }, boot.AddMinutes(2));

        Assert.Empty(adopted);
        Assert.Equal(2, after.CountFor("desk"));
        Assert.Equal(2500, after.EntriesFor("desk").Single(e => e.Identity == Chrome).Record.Left);
        Assert.Equal(1800, after.EntriesFor("desk").Single(e => e.Identity == Notepad).Record.Left);
        Assert.All(after.EntriesFor("desk"), e => Assert.True(e.ConfidentBinding));
    }

    [Fact]
    public void A_record_that_itself_fits_the_fallback_box_moves_nothing()
    {
        // A small utility window that genuinely lives in the top-left corner: both the remembered and the
        // observed rectangle fit the fallback box, so there is no squeeze to undo — just a window that opened
        // a little away from where it was. Leave it.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { S(0x100, Notepad, 100, 80) }, T0);
        m.PruneDead(_ => false, T0);

        var adopted = m.Capture("desk", new[] { S(0x900, Notepad, 0, 0) }, T0.AddMinutes(1));

        Assert.Empty(adopted);
        Assert.Equal(0, m.EntriesFor("desk")[0].Record.Left);
    }

    [Fact]
    public void A_squeezed_window_hanging_slightly_outside_the_box_still_counts_as_squeezed()
    {
        // GetWindowRect includes DWM's invisible resize border, so a window flush against the fallback desktop
        // reports a frame a few pixels outside it. That is still the squeeze.
        var m = new LayoutMemory();
        m.Capture("desk", new[] { W(0x100, Chrome, 2400, 300) }, T0);
        m.PruneDead(_ => false, T0);

        var squeezed = new CapturedWindow(new IntPtr(0x900), Chrome, "",
            new WindowRecord(-8, -8, 1032, 776, -8, -8, 1032, 776, NativeMethods.SW_SHOWNORMAL));
        var adopted = m.Capture("desk", new[] { squeezed }, T0.AddMinutes(1));

        var one = Assert.Single(adopted);
        Assert.Equal(2400, one.Target.Left);
    }

    [Fact]
    public void A_squeezed_maximized_window_is_adopted_by_its_restore_rectangle()
    {
        // A maximized window's on-screen rect is the whole monitor before and after a squeeze; what the fallback
        // damages is its restore rectangle (rcNormalPosition), which is also what the restore path rewrites.
        var remembered = new CapturedWindow(new IntPtr(0x100), Chrome, "",
            new WindowRecord(0, 0, 5120, 2088, 1000, 200, 3400, 1700, NativeMethods.SW_SHOWMAXIMIZED));

        var m = new LayoutMemory();
        m.Capture("desk", new[] { remembered }, T0);
        m.PruneDead(_ => false, T0);
        var squeezed = new CapturedWindow(new IntPtr(0x900), Chrome, "",
            new WindowRecord(0, 0, 5120, 2088, 0, 0, 1024, 740, NativeMethods.SW_SHOWMAXIMIZED));
        var adopted = m.Capture("desk", new[] { squeezed }, T0.AddMinutes(1));

        var one = Assert.Single(adopted);
        Assert.Equal(1000, one.Target.NormLeft);
        Assert.Equal(3400, one.Target.NormRight);

        // ...and a maximized window whose restore rect is fine is left alone, however it came back.
        var m2 = new LayoutMemory();
        m2.Capture("desk", new[] { remembered }, T0);
        m2.PruneDead(_ => false, T0);
        var fine = new CapturedWindow(new IntPtr(0x900), Chrome, "",
            new WindowRecord(0, 0, 5120, 2088, 1200, 300, 3000, 1600, NativeMethods.SW_SHOWMAXIMIZED));
        Assert.Empty(m2.Capture("desk", new[] { fine }, T0.AddMinutes(1)));
        Assert.Equal(1200, m2.EntriesFor("desk")[0].Record.NormLeft);
    }
}
