using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Disk persistence of the layout memory. Every test writes to its own throwaway file — the real
/// %LOCALAPPDATA%\Reframe\window-layouts.json is never touched.
/// </summary>
public class LayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReframeLayoutStoreTests", Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "window-layouts.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime T0 = new(2026, 7, 20, 10, 30, 0, DateTimeKind.Utc);

    private static CapturedWindow Win(int handle, string proc, string cls, string title, int x, int y, int showCmd = 1)
        => new(new IntPtr(handle), WindowIdentity.Create(proc, cls), title,
               new WindowRecord(x, y, x + 800, y + 600, x, y, x + 800, y + 600, showCmd));

    [Fact]
    public void Round_trip_preserves_identity_ordinal_geometry_and_timestamps()
    {
        var mem = new LayoutMemory();
        mem.Capture("desk", new[]
        {
            Win(0x100, "chrome", "Chrome_WidgetWin_1", "Inbox - Gmail", 10, 20),
            Win(0x200, "chrome", "Chrome_WidgetWin_1", "Docs", 300, 40),
            Win(0x300, "notepad", "Notepad", "todo.txt", 500, 60, showCmd: 3),
        }, T0);
        Assert.True(LayoutStore.Save(mem.ExportForDisk(), File_));

        var reloaded = new LayoutMemory();
        reloaded.ImportFromDisk(LayoutStore.Load(File_));

        var entries = reloaded.EntriesFor("desk");
        Assert.Equal(3, entries.Count);

        var chromes = entries.Where(e => e.Identity.ProcessName == "chrome").OrderBy(e => e.Ordinal).ToList();
        Assert.Equal(2, chromes.Count);
        Assert.Equal(new[] { 0, 1 }, chromes.Select(e => e.Ordinal));
        Assert.Equal("chrome_widgetwin_1", chromes[0].Identity.ClassName);
        Assert.Equal(10, chromes[0].Record.Left);
        Assert.Equal(810, chromes[0].Record.Right);
        Assert.Equal("Inbox - Gmail", chromes[0].Title); // stored for diagnostics, not used for matching
        Assert.Equal(T0, chromes[0].LastSeenUtc);

        var notepad = entries.Single(e => e.Identity.ProcessName == "notepad");
        Assert.Equal(3, notepad.Record.ShowCmd);
        Assert.Equal(500, notepad.Record.NormLeft);
    }

    [Fact]
    public void Loaded_records_are_unbound_and_reclaimed_only_by_identity()
    {
        var mem = new LayoutMemory();
        mem.Capture("desk", new[] { Win(0x100, "chrome", "Chrome_WidgetWin_1", "t", 10, 20) }, T0);
        LayoutStore.Save(mem.ExportForDisk(), File_);

        var reloaded = new LayoutMemory();
        reloaded.ImportFromDisk(LayoutStore.Load(File_));

        // The layout counts as "we know this display"...
        Assert.True(reloaded.HasSnapshot("desk"));
        Assert.Equal(1, reloaded.CountFor("desk"));
        // ...but nothing is actionable yet: last session's handle was never written to the file, so the record
        // cannot sneak into the HWND fast path.
        Assert.Empty(reloaded.GetRestorable("desk"));
        Assert.Empty(reloaded.GetRestorePlan("desk", new[] { new IntPtr(0x100) }));
        Assert.All(reloaded.EntriesFor("desk"), e => Assert.Equal(IntPtr.Zero, e.Handle));

        // The new session's Chrome (different handle) claims it by identity.
        int claimed = reloaded.Reclaim("desk", new[]
        {
            new LiveWindowRef(new IntPtr(0x9A0), WindowIdentity.Create("chrome", "chrome_widgetwin_1"))
        });
        Assert.Equal(1, claimed);
        Assert.Equal(10, reloaded.GetRestorePlan("desk", new[] { new IntPtr(0x9A0) })[0].Target.Left);
    }

    [Fact]
    public void Version_field_is_written_and_a_future_version_is_ignored()
    {
        var mem = new LayoutMemory();
        mem.Capture("desk", new[] { Win(0x100, "chrome", "Chrome_WidgetWin_1", "t", 10, 20) }, T0);
        LayoutStore.Save(mem.ExportForDisk(), File_);

        string json = File.ReadAllText(File_);
        Assert.Contains("\"Version\": 1", json);
        Assert.Equal(1, LayoutStore.Load(File_).Version);

        File.WriteAllText(File_, json.Replace("\"Version\": 1", "\"Version\": 99"));
        var future = LayoutStore.Load(File_);
        Assert.Empty(future.Layouts); // unreadable shape → start empty rather than misinterpret it
    }

    [Fact]
    public void Missing_file_loads_as_empty_memory()
    {
        var file = LayoutStore.Load(Path.Combine(_dir, "does-not-exist.json"));
        Assert.NotNull(file);
        Assert.Empty(file.Layouts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{ not json at all")]
    [InlineData("{\"Version\":1,\"Layouts\":[{\"DisplayKey\":\"desk\",\"Windows\":[{\"Process\":\"chr")] // half-written
    [InlineData("[1,2,3]")]
    public void Corrupt_or_truncated_files_degrade_to_empty_memory_without_throwing(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, content);

        var file = LayoutStore.Load(File_);
        Assert.Empty(file.Layouts);

        var mem = new LayoutMemory();
        mem.ImportFromDisk(file);
        Assert.Empty(mem.Keys);
        Assert.False(mem.HasSnapshot("desk"));
    }

    [Fact]
    public void Import_tolerates_a_null_file_and_junk_entries()
    {
        var mem = new LayoutMemory();
        mem.ImportFromDisk(null);
        Assert.Empty(mem.Keys);

        mem.ImportFromDisk(new LayoutFile
        {
            Layouts = new List<PersistedLayout>
            {
                new() { DisplayKey = "", Windows = { new PersistedWindow() } },        // no key
                new() { DisplayKey = LayoutKey.None, Windows = { new PersistedWindow() } }, // sentinel key
                new() { DisplayKey = "desk" },                                          // no windows
            }
        });
        Assert.Empty(mem.Keys);
    }

    [Fact]
    public void Save_replaces_atomically_and_leaves_no_temp_files()
    {
        var mem = new LayoutMemory();
        mem.Capture("desk", new[] { Win(0x100, "chrome", "Chrome_WidgetWin_1", "t", 10, 20) }, T0);
        Assert.True(LayoutStore.Save(mem.ExportForDisk(), File_));

        var mem2 = new LayoutMemory();
        mem2.Capture("desk", new[] { Win(0x100, "chrome", "Chrome_WidgetWin_1", "t", 77, 88) }, T0);
        Assert.True(LayoutStore.Save(mem2.ExportForDisk(), File_)); // overwrite an existing file

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        var reloaded = new LayoutMemory();
        reloaded.ImportFromDisk(LayoutStore.Load(File_));
        Assert.Equal(77, reloaded.EntriesFor("desk")[0].Record.Left);
    }

    [Fact]
    public void Save_reports_failure_instead_of_throwing_when_the_path_is_unusable()
    {
        // A directory where the file should be: the write cannot succeed, and must not take the worker down.
        Directory.CreateDirectory(File_);
        Assert.False(LayoutStore.Save(new LayoutFile(), File_));
    }

    [Fact]
    public void Legacy_pre_dpi_display_keys_load_safely_and_age_out_without_polluting_new_buckets()
    {
        // Files written before the DisplayKey carried a '#dpi' segment contain keys like "7680x2160@0,0*".
        // We deliberately do NOT migrate them (guessing which scale factor they were recorded at would
        // restore wrong geometry silently). This pins what we do instead: load them, keep them inert, let
        // the normal aging rules delete them.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """
        {
          "Version": 1,
          "Layouts": [
            {
              "DisplayKey": "7680x2160@0,0*",
              "Windows": [
                {
                  "Process": "chrome", "Class": "chrome_widgetwin_1", "Ordinal": 0, "Title": "old",
                  "Left": 10, "Top": 20, "Right": 810, "Bottom": 620,
                  "NormLeft": 10, "NormTop": 20, "NormRight": 810, "NormBottom": 620,
                  "ShowCmd": 1, "LastSeenUtc": "2026-01-05T08:00:00Z"
                }
              ]
            }
          ]
        }
        """);

        // 1. It loads. No exception, no data loss, no special-casing.
        var file = LayoutStore.Load(File_);
        var mem = new LayoutMemory();
        mem.ImportFromDisk(file);
        Assert.Equal(new[] { "7680x2160@0,0*" }, mem.Keys);
        Assert.True(mem.HasSnapshot("7680x2160@0,0*"));

        // 2. It does not pollute the bucket the same monitor produces today (at any scale factor): a key
        //    computed now always carries '#dpi', so the old bucket can never be matched or written into.
        string live150 = LayoutKey.Compute(new[] { new MonitorDesc("\\\\.\\DISPLAY1", true, 0, 0, 7680, 2160, 0, 0, 7680, 2160, 144) });
        string live175 = LayoutKey.Compute(new[] { new MonitorDesc("\\\\.\\DISPLAY1", true, 0, 0, 7680, 2160, 0, 0, 7680, 2160, 168) });
        Assert.False(mem.HasSnapshot(live150));
        Assert.False(mem.HasSnapshot(live175));
        Assert.Empty(mem.GetRestorable("7680x2160@0,0*")); // imported ⇒ unbound ⇒ nothing to act on

        // A capture under today's key creates its own bucket and leaves the orphan untouched.
        mem.Capture(live175, new[] { Win(0x100, "chrome", "chrome_widgetwin_1", "new", 500, 60) }, T0);
        Assert.Equal(1, mem.CountFor(live175));
        Assert.Equal(500, mem.EntriesFor(live175)[0].Record.Left);
        Assert.Equal(10, mem.EntriesFor("7680x2160@0,0*")[0].Record.Left); // untouched

        // 3. It ages out on its own: unbound + older than maxAge ⇒ dropped, and the empty key disappears.
        //    (The fresh bucket survives — this is aging, not a purge of old-format keys.)
        int dropped = mem.Trim(LayoutMemory.DefaultMaxPerKey, LayoutMemory.DefaultMaxAge, T0.AddDays(60));
        Assert.Equal(1, dropped);
        Assert.DoesNotContain("7680x2160@0,0*", mem.Keys);
        Assert.False(mem.HasSnapshot("7680x2160@0,0*"));
        Assert.True(mem.HasSnapshot(live175));

        // And the trimmed shape round-trips to disk without the orphan coming back.
        Assert.True(LayoutStore.Save(mem.ExportForDisk(), File_));
        var after = new LayoutMemory();
        after.ImportFromDisk(LayoutStore.Load(File_));
        Assert.Equal(new[] { live175 }, after.Keys);
    }

    [Fact]
    public void Real_store_path_sits_next_to_config_json()
    {
        // Path derivation only — nothing is read or written here.
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe"),
            LayoutStore.Dir);
        Assert.Equal("window-layouts.json", Path.GetFileName(LayoutStore.Path_));
    }
}
