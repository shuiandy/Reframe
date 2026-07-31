using System;
using System.Collections.Generic;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// The pure assignment algorithm that replaced "the record IS the HWND": which live window does each
/// remembered record belong to, now that a restarted app arrives with a brand-new handle — and, separately,
/// whether that pairing is trustworthy enough to move the window on the strength of it
/// (<see cref="WindowAssignment.Confident"/>).
/// </summary>
public class WindowMatcherTests
{
    private static WindowIdentity Id(string proc, string cls = "mainwnd") => WindowIdentity.Create(proc, cls);

    private static MemoryEntryRef Entry(int id, WindowIdentity identity, int ordinal = 0, int bound = 0, string title = "")
        => new(id, identity, ordinal, new IntPtr(bound), title);

    private static LiveWindowRef Live(int handle, WindowIdentity identity, string title = "")
        => new(new IntPtr(handle), identity, title);

    private static WindowAssignment For(IEnumerable<WindowAssignment> r, int handle)
        => r.Single(p => p.Handle == new IntPtr(handle));

    private static int IdFor(IEnumerable<WindowAssignment> r, int handle) => For(r, handle).Id;

    private static bool ConfidentFor(IEnumerable<WindowAssignment> r, int handle) => For(r, handle).Confident;

    [Fact]
    public void Unbound_record_is_claimed_by_the_restarted_window_of_the_same_identity()
    {
        // The bug this whole feature exists for: Chrome restarted at 12:33, so the record's old handle is gone.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(7, chrome) },              // unbound (its window died / came off disk)
            new[] { Live(0x5000, chrome) });         // the new Chrome process's window

        Assert.Single(result);
        Assert.Equal(7, IdFor(result, 0x5000));
        Assert.True(ConfidentFor(result, 0x5000));   // one record, one window: nothing else it could be
    }

    [Fact]
    public void Handle_fast_path_wins_over_identity_pairing()
    {
        // Same session, nothing restarted: the still-bound record must pair with its own handle even though
        // another record of the same identity has a lower ordinal and would otherwise sort first.
        var np = Id("notepad");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, np, ordinal: 0), Entry(2, np, ordinal: 1, bound: 0x300) },
            new[] { Live(0x300, np) });

        Assert.Single(result);
        Assert.Equal(2, IdFor(result, 0x300));       // the bound record, not the ordinal-0 one
    }

    [Fact]
    public void Every_record_keeps_its_own_handle_in_a_pure_fast_path_round()
    {
        var np = Id("notepad");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, np, 0, 0x100), Entry(2, np, 1, 0x200), Entry(3, np, 2, 0x300) },
            new[] { Live(0x300, np), Live(0x100, np), Live(0x200, np) });

        Assert.Equal(3, result.Count);
        Assert.Equal(1, IdFor(result, 0x100));
        Assert.Equal(2, IdFor(result, 0x200));
        Assert.Equal(3, IdFor(result, 0x300));
    }

    [Fact]
    public void Multiple_indistinguishable_windows_still_bind_by_ordinal_but_never_confidently()
    {
        // Three Chrome windows came back with new (and deliberately out-of-order) handles and nothing to tell
        // them apart. They still take a slot each — ordinal 0 gets the lowest handle, ordinal 1 the next,
        // ordinal 2 the highest, deterministic and needing no creation timestamps — so no duplicate records
        // pile up. But handle order is arbitrary across a restart, so none of it is evidence of *which* window
        // this is, and every pairing is non-confident: bookkeeping only, nothing may be moved.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(30, chrome, 2), Entry(10, chrome, 0), Entry(20, chrome, 1) },
            new[] { Live(0x900, chrome), Live(0x100, chrome), Live(0x500, chrome) });

        Assert.Equal(3, result.Count);
        Assert.Equal(10, IdFor(result, 0x100));
        Assert.Equal(20, IdFor(result, 0x500));
        Assert.Equal(30, IdFor(result, 0x900));
        Assert.All(result, a => Assert.False(a.Confident));
    }

    [Fact]
    public void More_candidates_than_records_leaves_the_surplus_windows_untouched()
    {
        // The user opened a third Chrome window that we have no memory of: we never invent a position for it.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0), Entry(2, chrome, 1) },
            new[] { Live(0x100, chrome), Live(0x200, chrome), Live(0x300, chrome) });

        Assert.Equal(2, result.Count);
        Assert.Equal(1, IdFor(result, 0x100));
        Assert.Equal(2, IdFor(result, 0x200));
        Assert.DoesNotContain(result, p => p.Handle == new IntPtr(0x300));
    }

    [Fact]
    public void More_records_than_candidates_leaves_the_surplus_records_unclaimed()
    {
        // Only one of the three remembered Chrome windows is back so far; the other two records stay for later.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0), Entry(2, chrome, 1), Entry(3, chrome, 2) },
            new[] { Live(0x700, chrome) });

        Assert.Single(result);
        Assert.Equal(1, IdFor(result, 0x700)); // lowest ordinal claims first
    }

    [Fact]
    public void A_handle_is_never_assigned_twice_and_a_record_never_takes_two_windows()
    {
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0, 0x100), Entry(2, chrome, 1), Entry(3, chrome, 2) },
            new[] { Live(0x100, chrome), Live(0x200, chrome) });

        Assert.Equal(2, result.Count);
        Assert.Equal(result.Select(p => p.Handle).Distinct().Count(), result.Count);
        Assert.Equal(result.Select(p => p.Id).Distinct().Count(), result.Count);
        Assert.Equal(1, IdFor(result, 0x100)); // fast path
        Assert.Equal(2, IdFor(result, 0x200)); // then the lowest free ordinal
    }

    [Fact]
    public void Records_of_a_different_identity_are_never_cross_matched()
    {
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var code = Id("code", "chrome_widgetwin_1"); // same class, different app
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0) },
            new[] { Live(0x100, code) });

        Assert.Empty(result);
    }

    [Fact]
    public void A_record_whose_window_is_alive_but_not_offered_is_left_alone()
    {
        // Record 1's window is alive but was filtered out this round (the borderless engine owns it / we just
        // moved it). Its geometry must not be handed to the *other* Chrome window instead.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0, bound: 0x100) },
            new[] { Live(0x200, chrome) });

        Assert.Empty(result);
    }

    [Fact]
    public void Unmatchable_identities_only_ever_pair_through_the_handle_fast_path()
    {
        // Process-name lookup failed for both sides: refuse to guess (identity grouping is skipped), but a
        // still-live handle binding is unambiguous and still pairs.
        var half = WindowIdentity.Create("", "generic_class");
        var byIdentity = WindowMatcher.Assign(
            new[] { Entry(1, half, 0) },
            new[] { Live(0x100, half) });
        Assert.Empty(byIdentity);

        var byHandle = WindowMatcher.Assign(
            new[] { Entry(1, half, 0, bound: 0x100) },
            new[] { Live(0x100, half) });
        Assert.Single(byHandle);
    }

    [Fact]
    public void Empty_inputs_produce_no_assignments()
    {
        var chrome = Id("chrome");
        Assert.Empty(WindowMatcher.Assign(Array.Empty<MemoryEntryRef>(), new[] { Live(1, chrome) }));
        Assert.Empty(WindowMatcher.Assign(new[] { Entry(1, chrome) }, Array.Empty<LiveWindowRef>()));
    }

    [Fact]
    public void Assignment_is_deterministic_regardless_of_input_order()
    {
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var np = Id("notepad");
        var entries = new[] { Entry(1, chrome, 0), Entry(2, chrome, 1), Entry(3, np, 0) };
        var live = new[] { Live(0x300, np), Live(0x200, chrome), Live(0x100, chrome) };

        var a = WindowMatcher.Assign(entries, live);
        // (Enumerable.Reverse, not the array/Span overload — the latter reverses in place and returns void.)
        var b = WindowMatcher.Assign(Enumerable.Reverse(entries).ToArray(), Enumerable.Reverse(live).ToArray());

        Assert.Equal(a.OrderBy(p => p.Id), b.OrderBy(p => p.Id));
        Assert.Equal(1, IdFor(a, 0x100));
        Assert.Equal(2, IdFor(a, 0x200));
        Assert.Equal(3, IdFor(a, 0x300));
    }

    // ---- Confidence: which pairings may actually move a window ----

    [Fact]
    public void A_group_unique_exact_title_pairs_confidently_and_overrides_ordinal_order()
    {
        // Two same-class windows, told apart by their captions. Note the titles cross the ordinal order: by
        // ordinal alone record 1 would have taken the low handle, which is exactly the wrong answer.
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, title: "QQ"), Entry(2, qq, 1, title: "图片查看器") },
            new[] { Live(0x100, qq, "图片查看器"), Live(0x200, qq, "QQ") });

        Assert.Equal(2, result.Count);
        Assert.Equal(1, IdFor(result, 0x200));
        Assert.Equal(2, IdFor(result, 0x100));
        Assert.All(result, a => Assert.True(a.Confident));
    }

    [Fact]
    public void A_title_worn_by_two_records_is_no_evidence_and_pairs_only_as_bookkeeping()
    {
        // The QQ shape in miniature: two remembered windows share a caption, so a window wearing it could be
        // either one. Bind (so records don't pile up), but never claim to know which.
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, title: "这是个什么群"), Entry(2, qq, 1, title: "这是个什么群") },
            new[] { Live(0x100, qq, "这是个什么群"), Live(0x200, qq, "这是个什么群") });

        Assert.Equal(2, result.Count);                       // both bound
        Assert.All(result, a => Assert.False(a.Confident));  // none movable
    }

    [Fact]
    public void A_title_worn_by_two_candidates_is_no_evidence_either()
    {
        // One record, but two live windows answer to its caption — the user opened the same chat twice.
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, title: "这是个什么群"), Entry(2, qq, 1, title: "QQ") },
            new[] { Live(0x100, qq, "这是个什么群"), Live(0x200, qq, "这是个什么群") });

        // "QQ" has no candidate; "这是个什么群" is duplicated among the candidates. Nothing is confident, and
        // the leftover is 2 records vs. 2 windows, so the 1:1 rule doesn't rescue it either.
        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.False(a.Confident));
    }

    [Fact]
    public void A_blank_caption_is_never_evidence_not_even_against_another_blank_one()
    {
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, title: "   "), Entry(2, qq, 1, title: "") },
            new[] { Live(0x100, qq, ""), Live(0x200, qq, "  ") });

        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.False(a.Confident));
    }

    [Fact]
    public void Title_matching_is_exact_never_a_prefix_or_a_case_fold()
    {
        // Fuzzy matching is what would put a chat window onto the image viewer's record; there is none.
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, title: "Chat"), Entry(2, qq, 1, title: "Album") },
            new[] { Live(0x100, qq, "Chat (3)"), Live(0x200, qq, "album") });

        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.False(a.Confident));
    }

    [Fact]
    public void Surrounding_whitespace_is_normalized_away_before_comparing()
    {
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, title: "  图片查看器 "), Entry(2, qq, 1, title: "这是个什么群") },
            new[] { Live(0x100, qq, "图片查看器"), Live(0x200, qq, "别的群") });

        Assert.Equal(1, IdFor(result, 0x100));
        Assert.True(ConfidentFor(result, 0x100));
        // ...and the leftover is then 1 record vs. 1 window, so it is confident too.
        Assert.Equal(2, IdFor(result, 0x200));
        Assert.True(ConfidentFor(result, 0x200));
    }

    [Fact]
    public void The_last_record_and_the_last_window_in_a_group_must_be_each_other()
    {
        // The scenario the whole feature was built for: a single-window app whose caption follows the content
        // (Chrome on a different tab, an editor with another file open). The titles do not match at all, and
        // that is fine — there is nothing else in the group either could be.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0, title: "Inbox - Gmail") },
            new[] { Live(0x400, chrome, "Reframe - GitHub") });

        Assert.Single(result);
        Assert.True(ConfidentFor(result, 0x400));
    }

    [Fact]
    public void A_confident_title_match_can_leave_a_confident_one_to_one_behind()
    {
        // Rules are applied in order: the unique caption settles one pair, and what is left is then 1:1 —
        // forced, and therefore also confident.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0, title: "Docs"), Entry(2, chrome, 1, title: "Mail") },
            new[] { Live(0x100, chrome, "Mail"), Live(0x200, chrome, "Somewhere else") });

        Assert.Equal(2, IdFor(result, 0x100));   // caption match
        Assert.Equal(1, IdFor(result, 0x200));   // the forced leftover
        Assert.All(result, a => Assert.True(a.Confident));
    }

    [Fact]
    public void An_unmatched_record_left_over_beside_two_windows_is_not_a_one_to_one()
    {
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0, title: "Docs") },
            new[] { Live(0x100, chrome, "A"), Live(0x200, chrome, "B") });

        Assert.Single(result);                            // one record, one slot taken
        Assert.False(result[0].Confident);                // ...but which of the two windows? unknown
    }

    [Fact]
    public void Title_evidence_never_crosses_identity_groups()
    {
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var code = Id("code", "chrome_widgetwin_1"); // same class, different app
        var result = WindowMatcher.Assign(
            new[] { Entry(1, chrome, 0, title: "README.md") },
            new[] { Live(0x100, code, "README.md") });

        Assert.Empty(result);
    }

    [Fact]
    public void The_handle_fast_path_is_confident_and_takes_its_window_out_of_the_ambiguous_pool()
    {
        // Three same-class windows; one is still bound from this session. That one is certain, and removing it
        // from the pool leaves 1 record + 1 window, which is then certain too.
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(1, qq, 0, bound: 0x100, title: "QQ"), Entry(2, qq, 1, title: "群 A") },
            new[] { Live(0x100, qq, "QQ"), Live(0x200, qq, "群 B") });

        Assert.Equal(2, result.Count);
        Assert.Equal(1, IdFor(result, 0x100));
        Assert.Equal(2, IdFor(result, 0x200));
        Assert.All(result, a => Assert.True(a.Confident));
    }

    [Fact]
    public void The_real_QQ_group_binds_every_window_and_confidently_pairs_none()
    {
        // Verbatim from the machine that showed the bug: QQ NT is Electron, so the main panel, every chat
        // window and the image viewer all sit in chrome_widgetwin_1 with wildly different sizes
        // (1015x1015 / 574x2002 / 2220x1487 / 1921x1152). QQ restarts to the tray (its panel is minimized, so
        // capture never offers it) and the user reopens two windows of the same group chat. Two records wear
        // that caption, so nothing is unique; four records against two windows is not 1:1 either. Every
        // window still gets a slot — but not one of them may be reshaped.
        var qq = Id("qq", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[]
            {
                Entry(0, qq, 0, title: "这是个什么群"),   // 1015x1015
                Entry(1, qq, 1, title: "QQ"),             //  574x2002  (main panel — in the tray right now)
                Entry(2, qq, 2, title: "这是个什么群"),   // 2220x1487
                Entry(3, qq, 3, title: "图片查看器"),     // 1921x1152  (not reopened)
            },
            new[] { Live(0x8800, qq, "这是个什么群"), Live(0x4400, qq, "这是个什么群") });

        Assert.Equal(2, result.Count);                      // both windows bound: no record pile-up
        Assert.All(result, a => Assert.False(a.Confident)); // and nothing is moved
    }
}
