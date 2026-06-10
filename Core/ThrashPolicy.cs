namespace Reframe.Core;

/// <summary>
/// 防拉锯状态(每个被接管窗口一份)。可变,由调用方持有并在串行上下文(<see cref="Watcher"/> 的
/// per-handle 锁)下传给 <see cref="ThrashPolicy.Evaluate"/>。字段公开是为了可单测、可序列化诊断。
/// </summary>
public sealed class ThrashState
{
    /// <summary>当前 10s 计数窗口的起点(UTC)。</summary>
    public DateTime WindowStartUtc;
    /// <summary>当前窗口内已重新应用的次数。</summary>
    public int Count;
    /// <summary>该窗口生命周期内累计已记的告警条数(达上限后静默,直到衰减或重建)。</summary>
    public int TotalWarns;
    /// <summary>最近一次记告警的时刻(UTC);用于 5min 衰减。默认 default 表示从未告警。</summary>
    public DateTime LastWarnUtc;
}

/// <summary>
/// 已接管窗口的"重新应用"节流策略(从 <see cref="Watcher"/> 抽出的纯逻辑,便于单测)。
/// 规则:
/// <list type="bullet">
/// <item>10s 滑动窗口内最多放行 <see cref="MaxApplies"/>(=3)次重新应用,超出本轮放过(返回 false)。</item>
/// <item>同一 10s 窗口最多记 1 条告警;整个窗口生命周期累计最多 <see cref="MaxWarns"/>(=2)条,此后静默。</item>
/// <item>衰减:开启新计数窗口时,若距上次告警已超过 <see cref="WarnDecay"/>(=5min)且上一窗口未触顶
///       (即期间无持续拉锯),把 <see cref="ThrashState.TotalWarns"/> 清零,避免长时间运行后永久失声。</item>
/// </list>
/// 无副作用、不碰 Win32、不打日志;告警与否经 <paramref name="warn"/> 回传给调用方决定如何呈现。
/// </summary>
public static class ThrashPolicy
{
    /// <summary>计数窗口长度:10s。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(10000);
    /// <summary>单个 10s 窗口内最多放行的重新应用次数。</summary>
    public const int MaxApplies = 3;
    /// <summary>窗口生命周期累计最多记的告警条数。</summary>
    public const int MaxWarns = 2;
    /// <summary>告警衰减时长:距上次告警超过此值且期间无拉锯,则重置累计告警数。</summary>
    public static readonly TimeSpan WarnDecay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 判定本次是否允许重新应用。<paramref name="warn"/> 回传"本次是否应记一条告警"。
    /// 调用方须在串行上下文中对同一 <paramref name="s"/> 调用(状态非线程安全)。
    /// </summary>
    /// <returns>true = 允许本次重新应用;false = 触顶,本轮放过。</returns>
    public static bool Evaluate(ThrashState s, DateTime nowUtc, out bool warn)
    {
        warn = false;

        // 当前窗口已过期:开新窗口。若上一窗口未触顶(Count < MaxApplies,即没有持续拉锯)
        // 且距上次告警已超过衰减阈值,则把累计告警清零,让长期运行后能重新提示。
        if (nowUtc - s.WindowStartUtc > Window)
        {
            bool quietLastWindow = s.Count < MaxApplies;
            if (quietLastWindow && s.TotalWarns > 0 &&
                s.LastWarnUtc != default && nowUtc - s.LastWarnUtc > WarnDecay)
            {
                s.TotalWarns = 0;
            }
            s.WindowStartUtc = nowUtc;
            s.Count = 0;
        }

        if (s.Count >= MaxApplies)
        {
            // 本窗口已触顶:仅当本窗口尚未告警(LastWarnUtc 不在本窗口内)且累计未达上限时记一条。
            bool warnedThisWindow = s.LastWarnUtc >= s.WindowStartUtc;
            if (!warnedThisWindow && s.TotalWarns < MaxWarns)
            {
                warn = true;
                s.TotalWarns++;
                s.LastWarnUtc = nowUtc;
            }
            return false;
        }

        s.Count++;
        return true;
    }
}
