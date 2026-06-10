using System;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// ThrashPolicy.Evaluate(ThrashState s, DateTime nowUtc, out bool warn) 纯逻辑(agent-A 合同靶点)。
/// 语义(见 ThrashPolicy 注释):
/// <list type="bullet">
///   <item>10s 滑动窗口内最多放行 MaxApplies(=3)次重新应用,第 4 次起本轮拒绝(返回 false)。</item>
///   <item>窗口过期(now - WindowStart > 10s)开新窗口,Count 归零。</item>
///   <item>同一窗口最多记 1 条告警;窗口生命周期累计最多 MaxWarns(=2)条,之后静默。</item>
///   <item>衰减:开新窗口时,若上一窗口未触顶(无持续拉锯)且距上次告警 &gt; 5min,清零 TotalWarns。</item>
/// </list>
/// 全程喂入固定 nowUtc,纯逻辑、无副作用。
/// </summary>
public class ThrashPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>新状态:窗口起点设为 nowUtc(模拟首次接管时初始化)。</summary>
    private static ThrashState FreshAt(DateTime now) => new() { WindowStartUtc = now };

    // ---- 窗口内放行/拒绝计数 ----

    [Fact(DisplayName = "窗口内:前 3 次放行(allow),第 4 次拒绝")]
    public void Window_FirstThreeAllow_FourthRejected()
    {
        var s = FreshAt(T0);

        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _));  // #1
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _));  // #2
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _));  // #3 触顶
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out _)); // #4 拒绝(同一 10s 窗口内)
    }

    [Fact(DisplayName = "窗口内:触顶后继续调用始终拒绝(第 5、6 次仍 false)")]
    public void Window_StaysRejectedAfterCap()
    {
        var s = FreshAt(T0);
        for (int i = 1; i <= 3; i++) Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(i), out _));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out _));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(5), out _));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(6), out _));
    }

    [Fact(DisplayName = "Count 在窗口内累加:第 3 次后 Count == MaxApplies")]
    public void Window_CountReachesMax()
    {
        var s = FreshAt(T0);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _);
        Assert.Equal(ThrashPolicy.MaxApplies, s.Count);
    }

    // ---- 跨窗口:Count 重置 ----

    [Fact(DisplayName = "跨窗口:>10s 后开新窗口,Count 归零,再次放行 3 次")]
    public void CrossWindow_ResetsCount_AllowsAgain()
    {
        var s = FreshAt(T0);
        // 第一窗口打满
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _);
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out _)); // 触顶

        // 跨过 10s 窗口(从 T0 起 > 10s):开新窗口,Count 应归零
        var t2 = T0.AddSeconds(11);
        Assert.True(ThrashPolicy.Evaluate(s, t2, out _));                 // 新窗口 #1 放行
        Assert.Equal(1, s.Count);
        Assert.Equal(t2, s.WindowStartUtc);                              // 窗口起点已重置
        Assert.True(ThrashPolicy.Evaluate(s, t2.AddSeconds(1), out _));  // #2
        Assert.True(ThrashPolicy.Evaluate(s, t2.AddSeconds(2), out _));  // #3
        Assert.False(ThrashPolicy.Evaluate(s, t2.AddSeconds(3), out _)); // #4 再次触顶
    }

    [Fact(DisplayName = "边界:恰好 10s(== Window)不算过期(用 > 比较),仍属同一窗口")]
    public void CrossWindow_ExactlyWindowBoundary_NotExpired()
    {
        var s = FreshAt(T0);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _); // 触顶
        // now - WindowStart == 10s,恰好不 > Window,仍是旧窗口 → 拒绝
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(10), out _));
        Assert.Equal(T0, s.WindowStartUtc); // 未开新窗口
    }

    // ---- 告警:第 1/2 次 warn=true,第 3 次 false(生命周期上限 2)----

    [Fact(DisplayName = "告警:连续三个打满的窗口 → 第 1、2 次 warn=true,第 3 次 warn=false")]
    public void Warn_FirstTwoTrue_ThirdFalse()
    {
        var s = FreshAt(T0);

        // 让一个窗口打满并触顶,返回触顶那次的 warn。窗口之间用 >10s 间隔强制开新窗口,
        // 且各窗口在告警后立即进入下一窗口(< 5min),以免触发衰减重置。
        bool SaturateAndGetWarn(DateTime baseTime)
        {
            Assert.True(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(1), out _)); // #1
            Assert.True(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(2), out _)); // #2
            Assert.True(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(3), out _)); // #3 触顶
            Assert.False(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(4), out bool warn)); // #4 触顶→可能告警
            return warn;
        }

        // 窗口 1:间隔 11s,持续拉锯(上一窗口触顶),不触发衰减
        bool w1 = SaturateAndGetWarn(T0);
        bool w2 = SaturateAndGetWarn(T0.AddSeconds(11));
        bool w3 = SaturateAndGetWarn(T0.AddSeconds(22));

        Assert.True(w1);   // 累计第 1 条
        Assert.True(w2);   // 累计第 2 条(达上限 MaxWarns=2)
        Assert.False(w3);  // 第 3 条被静默
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);
    }

    [Fact(DisplayName = "告警:同一窗口内多次触顶只记 1 条(第二次触顶 warn=false)")]
    public void Warn_OncePerWindow()
    {
        var s = FreshAt(T0);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _);
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out bool firstWarn));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(5), out bool secondWarn));
        Assert.True(firstWarn);    // 本窗口首次触顶记一条
        Assert.False(secondWarn);  // 同窗口再触顶不重复记
        Assert.Equal(1, s.TotalWarns);
    }

    [Fact(DisplayName = "告警:放行(未触顶)时永不告警")]
    public void Warn_NeverWhenAllowed()
    {
        var s = FreshAt(T0);
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out bool w1));
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out bool w2));
        Assert.False(w1);
        Assert.False(w2);
        Assert.Equal(0, s.TotalWarns);
    }

    // ---- 5min 衰减后再次可告警 ----

    [Fact(DisplayName = "衰减:静默(达上限)后,空闲 >5min 再次拉锯 → TotalWarns 清零并重新告警")]
    public void Decay_AfterFiveMinIdle_WarnsAgain()
    {
        var s = FreshAt(T0);

        bool SaturateAndGetWarn(DateTime baseTime)
        {
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(1), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(2), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(3), out _);
            Assert.False(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(4), out bool warn));
            return warn;
        }

        // 打满两窗口,耗尽 MaxWarns=2(第 1、2 条),第三窗口静默
        Assert.True(SaturateAndGetWarn(T0));
        Assert.True(SaturateAndGetWarn(T0.AddSeconds(11)));
        Assert.False(SaturateAndGetWarn(T0.AddSeconds(22)));
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);

        // 关键:上一窗口必须"未触顶"才允许衰减(quietLastWindow = Count < MaxApplies)。
        // 先来一个"安静窗口"(只放行 1 次、不触顶),并让它距上次告警 > 5min。
        // 上次告警发生在 T0+22 那段(约 T0+26s);取 6 分钟后开这个安静窗口。
        var quiet = T0.AddMinutes(6);
        Assert.True(ThrashPolicy.Evaluate(s, quiet, out _)); // 安静窗口:仅 1 次放行,Count=1 < 3
        // 此刻仍未衰减(衰减在"开新窗口"判定时发生,且要求上一窗口安静——即将在下一个新窗口生效)
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);

        // 再开一个新窗口(> 10s 后):上一窗口安静 + 距上次告警 > 5min → TotalWarns 清零,可再次告警
        var revived = quiet.AddSeconds(11);
        bool warnAgain = SaturateAndGetWarn(revived);
        Assert.True(warnAgain);            // 衰减后重新告警
        Assert.Equal(1, s.TotalWarns);     // 从 0 重新累计到 1
    }

    [Fact(DisplayName = "衰减不触发:持续拉锯(上一窗口触顶)即便跨 5min 也不清零")]
    public void Decay_NotTriggeredWhenStillThrashing()
    {
        var s = FreshAt(T0);

        bool SaturateAndGetWarn(DateTime baseTime)
        {
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(1), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(2), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(3), out _);
            Assert.False(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(4), out bool warn));
            return warn;
        }

        // 耗尽告警额度
        Assert.True(SaturateAndGetWarn(T0));
        Assert.True(SaturateAndGetWarn(T0.AddSeconds(11)));
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);

        // 即便距上次告警已 > 5min,但下一窗口"仍触顶"(上一窗口 Count==MaxApplies,非安静)
        // → 不满足 quietLastWindow,不清零,继续静默。
        bool warn3 = SaturateAndGetWarn(T0.AddMinutes(6));
        Assert.False(warn3);
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns); // 仍是上限,未被重置
    }
}
