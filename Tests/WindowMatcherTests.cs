using System;
using System.Collections.Generic;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// The pure assignment algorithm that replaced "the record IS the HWND": which live window does each
/// remembered record belong to, now that a restarted app arrives with a brand-new handle.
/// </summary>
public class WindowMatcherTests
{
    private static WindowIdentity Id(string proc, string cls = "mainwnd") => WindowIdentity.Create(proc, cls);

    private static MemoryEntryRef Entry(int id, WindowIdentity identity, int ordinal = 0, int bound = 0)
        => new(id, identity, ordinal, new IntPtr(bound));

    private static LiveWindowRef Live(int handle, WindowIdentity identity)
        => new(new IntPtr(handle), identity);

    private static int IdFor(IEnumerable<(IntPtr Handle, int Id)> r, int handle)
        => r.Single(p => p.Handle == new IntPtr(handle)).Id;

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
    public void Multiple_windows_of_one_identity_pair_by_ordinal_against_ascending_handles()
    {
        // Three Chrome windows came back with new (and deliberately out-of-order) handles: ordinal 0 gets the
        // lowest handle, ordinal 1 the next, ordinal 2 the highest — deterministic, no creation timestamps.
        var chrome = Id("chrome", "chrome_widgetwin_1");
        var result = WindowMatcher.Assign(
            new[] { Entry(30, chrome, 2), Entry(10, chrome, 0), Entry(20, chrome, 1) },
            new[] { Live(0x900, chrome), Live(0x100, chrome), Live(0x500, chrome) });

        Assert.Equal(3, result.Count);
        Assert.Equal(10, IdFor(result, 0x100));
        Assert.Equal(20, IdFor(result, 0x500));
        Assert.Equal(30, IdFor(result, 0x900));
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
}
