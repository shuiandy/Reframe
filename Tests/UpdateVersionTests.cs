using Reframe.Services;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// UpdateVersion: the parse/compare rules that decide whether the app offers an update at all.
/// The load-bearing property is conservatism — anything not understood must read as "no update",
/// because the alternative is prompting a download on the strength of a string we misread.
/// </summary>
public class UpdateVersionTests
{
    // ------------------------------------------------------------------ parsing

    [Theory(DisplayName = "Parses the spellings both sides of the comparison actually use")]
    [InlineData("1.3.1", 1, 3, 1)]
    [InlineData("v1.3.1", 1, 3, 1)]        // GitHub release tag
    [InlineData("V1.3.1", 1, 3, 1)]
    [InlineData("1.3.1+9f2c1ab", 1, 3, 1)] // InformationalVersion with the source revision appended
    [InlineData("v1.3.1+9f2c1ab", 1, 3, 1)]
    [InlineData("  v1.3.1  ", 1, 3, 1)]
    [InlineData("1.3.1.0", 1, 3, 1)]       // Assembly.GetName().Version fallback: revision ignored
    [InlineData("1.3", 1, 3, 0)]           // fewer components: the missing ones are zero
    [InlineData("2", 2, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("0.0.1", 0, 0, 1)]
    public void Parses(string text, int major, int minor, int patch)
    {
        Assert.True(UpdateVersion.TryParse(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Theory(DisplayName = "Rejects anything not understood (which downstream means 'no update')")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("+abc")]
    [InlineData("1.3.1-rc1")]      // pre-release tail
    [InlineData("1.3.1-beta.2")]
    [InlineData("1.2.3.4.5")]      // five components
    [InlineData("1..3")]           // empty component
    [InlineData("1.3.")]
    [InlineData(".1.3")]
    [InlineData("1.3.x")]
    [InlineData("one.two.three")]
    [InlineData("-1.0.0")]
    [InlineData("1.-3.1")]
    [InlineData("1 . 3 . 1")]
    [InlineData("1.3.1e2")]
    [InlineData("99999999999.0.0")] // overflows int
    [InlineData("latest")]
    [InlineData("vNext")]
    public void Rejects(string? text)
    {
        Assert.False(UpdateVersion.TryParse(text, out var v));
        Assert.Equal(default, v);
    }

    [Fact(DisplayName = "ToString() is the canonical three-component form used to build asset names")]
    public void ToStringIsCanonical()
    {
        Assert.True(UpdateVersion.TryParse("v1.3.1+abc", out var v));
        Assert.Equal("1.3.1", v.ToString());

        Assert.True(UpdateVersion.TryParse("1.3", out var short_));
        Assert.Equal("1.3.0", short_.ToString());
    }

    // ------------------------------------------------------------------ comparison

    [Theory(DisplayName = "Numeric comparison, component by component (not string ordering)")]
    [InlineData("1.3.1", "1.3.2", -1)]
    [InlineData("1.3.2", "1.3.1", 1)]
    [InlineData("1.3.1", "1.4.0", -1)]
    [InlineData("1.9.9", "2.0.0", -1)]
    [InlineData("1.3.1", "1.3.1", 0)]
    [InlineData("1.3.9", "1.3.10", -1)]   // "9" > "10" as strings; must not be
    [InlineData("2.0.0", "10.0.0", -1)]   // ditto
    public void Compares(string left, string right, int expectedSign)
    {
        Assert.True(UpdateVersion.TryParse(left, out var a));
        Assert.True(UpdateVersion.TryParse(right, out var b));
        Assert.Equal(expectedSign, Math.Sign(UpdateVersion.Compare(a, b)));
        Assert.Equal(-expectedSign, Math.Sign(UpdateVersion.Compare(b, a)));
    }

    [Theory(DisplayName = "IsNewer: only a strictly greater, parseable tag counts")]
    [InlineData("1.3.1", "v1.3.2", true)]
    [InlineData("1.3.1", "v1.4.0", true)]
    [InlineData("1.3.1", "v2.0.0", true)]
    [InlineData("0.0.1", "v1.3.1", true)]
    [InlineData("1.3.1", "v1.3.1", false)]  // same version: not newer
    [InlineData("1.3.1", "v1.3.0", false)]  // older release (e.g. a rollback): not newer
    [InlineData("1.3.1+9f2c1ab", "v1.3.1", false)] // build metadata does not make it older
    [InlineData("1.3.1.0", "v1.3.1", false)]       // 4-component current vs 3-component tag
    public void IsNewer_Cases(string current, string tag, bool expected)
        => Assert.Equal(expected, UpdateVersion.IsNewer(current, tag));

    [Theory(DisplayName = "IsNewer is conservative: an unparseable side is never 'newer'")]
    [InlineData("1.3.1", "v2.0.0-rc1")]  // tag we cannot read
    [InlineData("1.3.1", "latest")]
    [InlineData("1.3.1", null)]
    [InlineData("1.3.1", "")]
    [InlineData(null, "v9.9.9")]         // current version we cannot read
    [InlineData("", "v9.9.9")]
    [InlineData("unknown", "v9.9.9")]
    public void IsNewer_UnparseableIsNeverNewer(string? current, string? tag)
        => Assert.False(UpdateVersion.IsNewer(current, tag));

    [Fact(DisplayName = "Equality / ordering operators agree with Compare")]
    public void OperatorsAgree()
    {
        var a = new UpdateVersion(1, 3, 1);
        var b = new UpdateVersion(1, 3, 2);

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= new UpdateVersion(1, 3, 1));
        Assert.True(a >= new UpdateVersion(1, 3, 1));
        Assert.True(a == new UpdateVersion(1, 3, 1));
        Assert.True(a != b);
        Assert.Equal(a.GetHashCode(), new UpdateVersion(1, 3, 1).GetHashCode());
    }
}
