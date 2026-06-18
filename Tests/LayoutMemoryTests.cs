using System.Collections.Generic;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

public class LayoutMemoryTests
{
    private static (IntPtr, WindowRecord) W(int handle, int x, int y, int w = 100, int h = 100, int showCmd = 1)
        => (new IntPtr(handle), new WindowRecord(x, y, x + w, y + h, showCmd));

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
}
