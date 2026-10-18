using System.Buffers;

namespace QQT.Core;

public sealed partial class GameSimulation
{
    // 世界只借用计划中的位置内存；宿主必须保留该轮计划直到 IsExecuting=false。
    private readonly PlayerAction[] _tickPlans = ArrayPool<PlayerAction>.Shared.Rent(4);
    private bool _executing, _firstBatch;
    private RationalTime _tickEnd;
    public bool IsExecuting => _executing;
    public int TickLimit { get; set; }

    public void BeginTick(ReadOnlySpan<PlayerAction> plans)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ended || _executing) throw new InvalidOperationException("当前不能开始新的 tick。");
        if (plans.Length != PlayerCount) throw new InvalidDataException("需要实际参赛人数的完整计划。");
        PrepareRound();
        for (int i = 0; i < PlayerCount; i++)
        {
            if (plans[i].PlayerId != i + 1) throw new InvalidDataException("计划编号顺序非法。");
            ValidatePlan(plans[i]);
        }
        plans.CopyTo(_tickPlans);
        _roundPrepared = false; _phase = SimulationPhase.Execution;
        _attempted.AsSpan(0, PlayerCount * Size).Clear(); _segments.AsSpan(0, PlayerCount).Clear();
        _tickEnd = ++Tick; _firstBatch = _executing = true;
        for (int i = 0; i < PlayerCount; i++) if (!_players[i].Alive)
        {
            Add(new(EventKind.ActionIgnored, Time, i + 1, 0, _players[i].Position, _players[i].Position, false, Tick, SkipReason.Dead));
            _attempted.AsSpan(i * Size, Size).Fill(true);
        }
    }

    /// <summary>推进一个完整同刻批次。断线在跨线归属判定后、到期爆炸前淘汰；整批爆炸后统一判胜负。</summary>
    public void AdvanceEvent(int disconnectedMask = 0)
    {
        if (!_executing) throw new InvalidOperationException("没有正在执行的 tick。");
        bool origin = _firstBatch; _firstBatch = false;
        if (!origin)
        {
            var next = _tickEnd;
            for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].DueAt > Time && _bubbles[b].DueAt < next) next = _bubbles[b].DueAt;
            for (int i = 0; i < PlayerCount; i++) if (_players[i].Alive && _segments[i].Active)
            {
                ref var segment = ref _segments[i];
                var at = segment.Start + new RationalTime(1, segment.Crossed || segment.Returning ? _players[i].Speed : checked(2L * _players[i].Speed));
                if (at < next) next = at;
            }
            AdvancePoints(next); Time = next;
            // 所有跨线使用清理前环境，不能被同刻新放置追溯阻挡。
            for (int i = 0; i < PlayerCount; i++)
            {
                ref var player = ref _players[i]; ref var segment = ref _segments[i];
                if (!player.Alive || !segment.Active) continue;
                var half = segment.Start + new RationalTime(1, checked(2L * player.Speed));
                var center = segment.Start + new RationalTime(1, player.Speed);
                if (!segment.Crossed && !segment.Returning && Time == half)
                {
                    if (!segment.Target.InBounds || _terrain[segment.Target.Index] != Cell.Empty || HasBubble(segment.Target))
                    {
                        segment.Returning = true;
                        Add(new(EventKind.Blocked, Time, player.Id, 0, segment.Target, segment.Origin, false));
                        Add(new(EventKind.ReturnStarted, Time, player.Id, 0, segment.Origin, segment.Origin, true));
                    }
                    else
                    {
                        segment.Crossed = true; player.Position = segment.Target;
                        Add(new(EventKind.Entered, Time, player.Id, 0, player.Position, segment.Origin, true));
                    }
                }
                if (Time == center)
                {
                    segment.Active = false;
                    Add(new(segment.Returning ? EventKind.Returned : EventKind.Move, Time, player.Id, 0, player.Position, segment.Origin, true));
                }
            }
        }
        int newlyDisconnected = MarkDisconnected(disconnectedMask);
        ExplodeDue();
        RecordDisconnected(newlyDisconnected);
        var plans = _tickPlans.AsSpan(0, PlayerCount);
        if (ResolveOutcome()) { FinishTick(); return; }
        SkipRemaining(plans, true);
        PickItems(); AttemptCenters(plans, origin);
        if (Time == _tickEnd)
        {
            SkipRemaining(plans); RecordStates();
            if (Tick >= TickLimit)
            {
                Ended = true; WinnerId = null; EndTick = Time;
                int alive = 0; foreach (var p in Players) if (p.Alive) alive++;
                Add(new(EventKind.MatchEnded, Time, 0, 0, default, default, true, alive));
            }
            FinishTick(); return;
        }
        for (int i = 0; i < PlayerCount; i++) if (!_segments[i].Active && !_segments[i].Returning) StartSegment(i, plans[i].Direction, _tickEnd);
        RecordStates();
    }

    private void FinishTick()
    {
        _executing = false;
        _tickPlans.AsSpan(0, PlayerCount).Clear();
    }

    /// <summary>等待阶段掉线不推进时间、不触发到期或激活新轮属性。宿主串行调用。</summary>
    public void Disconnect(int disconnectedMask)
    {
        if (_executing || Ended) throw new InvalidOperationException("执行期间请在下一事件批次传入断线掩码。");
        int marked = MarkDisconnected(disconnectedMask);
        RecordDisconnected(marked);
        if (marked != 0) ResolveOutcome();
    }

    private int MarkDisconnected(int mask)
    {
        int marked = 0;
        for (int i = 0; i < PlayerCount; i++) if ((mask & (1 << i)) != 0 && _players[i].Alive)
        {
            _players[i].Hit = true; _players[i].DeathTick = Time;
            _segments[i].Active = false;
            marked |= 1 << i;
        }
        return marked;
    }

    private void RecordDisconnected(int mask)
    {
        for (int i = 0; i < PlayerCount; i++) if ((mask & (1 << i)) != 0)
        {
            ref var p = ref _players[i]; p.OwnedBubblesAtDeath = Owned(p.Id);
            Add(new(EventKind.Disconnected, Time, p.Id, 0, p.Position, p.Position, false));
            Add(new(EventKind.Death, Time, p.Id, 0, p.Position, p.Position, false, p.OwnedBubblesAtDeath));
        }
    }
}
