using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「播放中途卡顿自愈」的决策规则(2026-11-14, issue #81)。
///
/// 这里锁住的是**行为契约**: 什么时候只提示、什么时候真的重建 input、
/// 最多重建几次、以及"位置一动就把预算还回去"。真实时间轴由播放器 500ms 心跳驱动,
/// 所以测试里直接喂毫秒时间戳 —— 不需要真的等。
/// </summary>
public class StallRecoveryPolicyTests
{
    [Fact]
    public void 前六秒保持安静()
    {
        var p = new StallRecoveryPolicy();
        Assert.Equal(StallRecoveryAction.None, p.Tick(0, frozen: true, recoverable: true));
        Assert.Equal(StallRecoveryAction.None, p.Tick(3_000, frozen: true, recoverable: true));
        Assert.Equal(StallRecoveryAction.None, p.Tick(5_999, frozen: true, recoverable: true));
        Assert.Equal(0, p.Rebuilds);
    }

    [Fact]
    public void 六秒开始提示_十秒才重建()
    {
        var p = new StallRecoveryPolicy();
        p.Tick(0, true, true);

        Assert.Equal(StallRecoveryAction.ShowHint, p.Tick(StallRecoveryPolicy.HintAfterMs, true, true));
        Assert.Equal(StallRecoveryAction.ShowHint, p.Tick(9_000, true, true));
        Assert.Equal(0, p.Rebuilds);   // 还没到 10 秒, 绝不能动播放器

        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(StallRecoveryPolicy.RebuildAfterMs, true, true));
        Assert.Equal(1, p.Rebuilds);
        Assert.False(p.GaveUp);
    }

    [Fact]
    public void 重建后按退避重试_最多三次然后放弃()
    {
        var p = new StallRecoveryPolicy();
        p.Tick(0, true, true);

        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(10_000, true, true));      // 第 1 次
        Assert.Equal(StallRecoveryAction.None, p.Tick(11_000, true, true));         // 退避中
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(18_000, true, true));      // 第 2 次(+8s)
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(33_000, true, true));      // 第 3 次(+15s)
        Assert.Equal(3, p.Rebuilds);

        Assert.Equal(StallRecoveryAction.GiveUp, p.Tick(58_000, true, true));       // +25s 仍是冻的
        Assert.True(p.GaveUp);
        // 放弃之后不再反复要重建(避免 UI 被反复刷成失败态)
        Assert.Equal(StallRecoveryAction.None, p.Tick(90_000, true, true));
    }

    [Fact]
    public void 重建后短暂推进不还预算_连续健康二十秒才还()
    {
        var p = new StallRecoveryPolicy();
        p.Tick(0, true, true);
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(10_000, true, true));
        Assert.Equal(1, p.Rebuilds);

        p.OnAdvance();     // 重建后从 0 定位到目标: 位置确实推进了一次
        Assert.False(p.IsFrozen);
        Assert.Equal(1, p.Rebuilds);   // 但预算不能立刻归零

        // 只健康了几秒: 仍保留第 1 次重建后的预算
        Assert.Equal(StallRecoveryAction.None, p.Tick(11_000, false, true));
        Assert.Equal(StallRecoveryAction.None, p.Tick(20_000, false, true));
        Assert.Equal(1, p.Rebuilds);

        // 连续正常播放满 20s: 预算归还
        Assert.Equal(StallRecoveryAction.None, p.Tick(31_000, false, true));
        Assert.Equal(0, p.Rebuilds);

        // 下一次卡顿重新拥有完整 3 次机会
        p.Tick(100_000, true, true);
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(110_000, true, true));
        Assert.Equal(1, p.Rebuilds);
    }

    [Fact]
    public void 不可动播放器时只提示不重建()
    {
        var p = new StallRecoveryPolicy();
        p.Tick(0, true, true);
        // 用户正在拖进度条 / 播放器不在可恢复状态
        Assert.Equal(StallRecoveryAction.ShowHint, p.Tick(10_000, true, recoverable: false));
        Assert.Equal(0, p.Rebuilds);

        // 条件恢复后立刻补上重建
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(10_500, true, recoverable: true));
    }

    [Fact]
    public void 重建途中位置不再冻结不会清掉预算()
    {
        // 重建后 LibVLC 会先进入 Opening/Buffering, 位置本来就不动 —— 调用方会走
        // "不冻结"分支(OnUnfrozen)。它**不能**把重建次数清掉, 否则会无限重建。
        var p = new StallRecoveryPolicy();
        p.Tick(0, true, true);
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(10_000, true, true));
        Assert.Equal(1, p.Rebuilds);

        p.OnUnfrozen();
        Assert.False(p.IsFrozen);
        Assert.Equal(1, p.Rebuilds);   // 预算还在

        // 重新开始计时, 但仍受"第 1 次重建后的退避"约束
        Assert.Equal(StallRecoveryAction.None, p.Tick(12_000, true, true));
        Assert.Equal(StallRecoveryAction.Rebuild, p.Tick(18_000, true, true));
        Assert.Equal(2, p.Rebuilds);
    }
}
