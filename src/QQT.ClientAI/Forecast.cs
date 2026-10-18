using System.Buffers;
using System.Numerics;
using QQT.Core;

namespace QQT.ClientAI;

/// <summary>441 格位图，作为值对象嵌入事件／搜索节点，无临时集合分配。</summary>
public struct CellBits
{
    private ulong _a, _b, _c, _d, _e, _f, _g;
    public readonly bool Has(int i) => ((Get(i >> 6) >> (i & 63)) & 1) != 0;
    private readonly ulong Get(int n) => n switch { 0 => _a, 1 => _b, 2 => _c, 3 => _d, 4 => _e, 5 => _f, _ => _g };
    public void Add(int i)
    {
        ulong bit = 1UL << (i & 63);
        switch (i >> 6) { case 0: _a |= bit; break; case 1: _b |= bit; break; case 2: _c |= bit; break; case 3: _d |= bit; break; case 4: _e |= bit; break; case 5: _f |= bit; break; default: _g |= bit; break; }
    }
    public readonly int Count => BitOperations.PopCount(_a) + BitOperations.PopCount(_b) + BitOperations.PopCount(_c) + BitOperations.PopCount(_d) + BitOperations.PopCount(_e) + BitOperations.PopCount(_f) + BitOperations.PopCount(_g);
    public readonly int ExceptCount(in CellBits other) => BitOperations.PopCount(_a & ~other._a) + BitOperations.PopCount(_b & ~other._b) + BitOperations.PopCount(_c & ~other._c) + BitOperations.PopCount(_d & ~other._d) + BitOperations.PopCount(_e & ~other._e) + BitOperations.PopCount(_f & ~other._f) + BitOperations.PopCount(_g & ~other._g);
}

public readonly record struct Plan(Direction Direction, uint Placements)
{
    public int Count => BitOperations.PopCount(Placements);
    public Position At(Position origin, int distance) => Direction switch
    {
        Direction.Up => new(origin.X, origin.Y - distance), Direction.Down => new(origin.X, origin.Y + distance),
        Direction.Left => new(origin.X - distance, origin.Y), Direction.Right => new(origin.X + distance, origin.Y), _ => origin
    };
}
public struct ForecastBubble
{
    public Position Position;
    public int Owner, Range;
    public RationalTime Placed, Due, Exploded;
    public bool Alive, Root, Uncertain;
}
public readonly record struct BlastBatch(RationalTime Time, CellBits Coverage);
public readonly record struct MotionResult(bool Alive, Position Position, RationalTime DeathAt, int Pickups, int RangeGain, int SpeedGain);

/// <summary>
/// 每个候选行动建立一次事件时间线，后续无新增放置的逃生分支共享它。
/// 原数组只在本实例租期内使用；不把预测变成服务端事实。
/// </summary>
public sealed class Forecast : IDisposable
{
    private readonly Cell[] _terrain = ArrayPool<Cell>.Shared.Rent(441);
    private readonly RationalTime[] _clearedAt = ArrayPool<RationalTime>.Shared.Rent(441);
    private ForecastBubble[] _bubbles = ArrayPool<ForecastBubble>.Shared.Rent(128);
    private BlastBatch[] _blasts = ArrayPool<BlastBatch>.Shared.Rent(128);
    private readonly bool[] _pending = ArrayPool<bool>.Shared.Rent(128);
    private bool[]? _largePending;
    private CellBits _cleared, _picked, _rootRemovedItems;
    private int _count, _blastCount, _owner, _range, _futureRange;
    private RationalTime _now, _start, _rootEnd;
    private int _counterOwner;
    private Position _counterPosition;
    private RationalTime _counterAt;
    private bool _counterDone;
    private WorldMemory _memory = null!;
    private bool _disposed;
    public MotionResult RootMotion { get; private set; }
    public int RootPlaced { get; private set; }
    public int NewBubbleStart { get; private set; }
    public CellBits Destroyed => _cleared;
    public ReadOnlySpan<ForecastBubble> Bubbles => _bubbles.AsSpan(0, _count);
    public ReadOnlySpan<BlastBatch> Blasts => _blasts.AsSpan(0, _blastCount);
    public RationalTime End { get; private set; }

    public void Build(WorldMemory memory, Plan plan, int horizon, int counterBombOwner = 0, Position? counterPosition = null, RationalTime? counterAt = null)
    {
        _memory = memory; memory.Terrain.AsSpan(0, 441).CopyTo(_terrain);
        _cleared = _picked = default; _count = _blastCount = RootPlaced = 0;
        _owner = memory.Self.Id; _range = _futureRange = memory.Self.Range;
        _start = _now = memory.Time; _rootEnd = _start + 1; End = _start + horizon;
        _counterOwner = counterBombOwner; _counterDone = counterBombOwner == 0;
        _counterPosition = counterPosition ?? (counterBombOwner > 0 ? memory.Enemies[counterBombOwner].Position : default);
        _counterAt = counterAt ?? _start;
        foreach (var b in memory.Bubbles)
            Add(new ForecastBubble { Position = b.Position, Owner = b.Owner, Range = b.Range, Placed = _start - 1,
                Due = b.Due, Alive = true, Uncertain = !b.Known || b.Estimated });
        NewBubbleStart = _count;
        RootMotion = RunRoot(plan);
        _rootRemovedItems = _picked;
        _futureRange = _range + RootMotion.RangeGain;
        while (NextDue(End) is { } due) { _now = due; Explode(); }
    }

    private void Add(ForecastBubble bubble)
    {
        if (_count == _bubbles.Length)
        {
            var next = ArrayPool<ForecastBubble>.Shared.Rent(checked(_count * 2));
            _bubbles.AsSpan(0, _count).CopyTo(next); ArrayPool<ForecastBubble>.Shared.Return(_bubbles); _bubbles = next;
        }
        _bubbles[_count++] = bubble;
    }
    private RationalTime? NextDue(RationalTime limit)
    {
        RationalTime? result = null;
        for (int i = 0; i < _count; i++) if (_bubbles[i].Alive && _bubbles[i].Due <= limit && (result is null || _bubbles[i].Due < result.Value)) result = _bubbles[i].Due;
        return result;
    }
    private bool HasBubble(Position p)
    {
        for (int i = 0; i < _count; i++) if (_bubbles[i].Alive && _bubbles[i].Position == p) return true;
        return false;
    }
    private int Owned()
    {
        int n = 0; for (int i = 0; i < _count; i++) if (_bubbles[i].Alive && _bubbles[i].Owner == _owner) n++;
        // 自身总数免费可知；不可因视野外未定位的泡泡而虚增容量。
        int knownAtStart = 0;
        foreach (var b in _memory.Bubbles) if (b.Owner == _owner) knownAtStart++;
        return n + Math.Max(0, _memory.Self.Active - knownAtStart);
    }

    private MotionResult RunRoot(Plan plan)
    {
        var position = _memory.Self.Position; var target = position;
        int speed = _memory.Self.Speed, step = 0, pickups = 0, rangeGain = 0, speedGain = 0;
        bool moving = false, crossed = false, returning = false;
        RationalTime segmentStart = _start;
        uint attempted = 0;
        while (true)
        {
            // 跨线按处理前障碍检查，随后才结算同刻爆炸；失败全程仍归原格。
            if (moving)
            {
                var half = segmentStart + new RationalTime(1, 2L * speed);
                var center = segmentStart + new RationalTime(1, speed);
                if (!crossed && !returning && _now == half)
                {
                    if (!target.InBounds || _terrain[target.Index] != Cell.Empty || HasBubble(target)) returning = true;
                    else { crossed = true; position = target; }
                }
                if (_now == center) { moving = false; if (!returning) step++; }
            }
            var coverage = Explode();
            if (coverage.Has(position.Index)) return new(false, position, _now, pickups, rangeGain, speedGain);
            if (!_counterDone && _now == _counterAt)
            {
                _counterDone = true;
                ref var e = ref _memory.Enemies[_counterOwner]; int released = 0;
                foreach (var b in Bubbles) if (b.Owner == _counterOwner && !b.Alive) released++;
                // 容量可能在本轮途中释放，不能仅因轮初满容量就忽略沿途放置反击。
                if (_counterPosition.InBounds && _terrain[_counterPosition.Index] == Cell.Empty && e.Active - released < e.Capacity)
                    Add(new ForecastBubble { Position = _counterPosition, Owner = _counterOwner, Range = e.Range, Placed = _now,
                        Due = _now + 3, Alive = true, Uncertain = true });
            }
            if (!moving)
            {
                if (!_picked.Has(position.Index) && _memory.Items[position.Index] != ItemType.None)
                {
                    _picked.Add(position.Index); pickups++;
                    if (_memory.Items[position.Index] == ItemType.Range) rangeGain++;
                    if (_memory.Items[position.Index] == ItemType.Speed) speedGain++;
                }
                uint bit = 1U << step;
                if (!returning && (plan.Placements & bit) != 0 && (attempted & bit) == 0)
                {
                    attempted |= bit;
                    if (Owned() < _memory.Self.Capacity)
                    {
                        Add(new ForecastBubble { Position = position, Owner = _owner, Range = _range, Placed = _now,
                            Due = _now + _memory.Self.Lifetime, Alive = true, Root = true });
                        RootPlaced++;
                    }
                }
                if (_now == _rootEnd) return new(true, position, default, pickups, rangeGain, speedGain);
                if (!returning && plan.Direction != Direction.Wait)
                {
                    target = position.Next(plan.Direction); segmentStart = _now;
                    moving = true; crossed = false;
                }
            }
            var next = _rootEnd;
            if (moving)
            {
                var motion = segmentStart + new RationalTime(1, crossed || returning ? speed : 2L * speed);
                if (motion < next) next = motion;
            }
            if (NextDue(next) is { } due) next = due;
            if (!_counterDone && _counterAt > _now && _counterAt < next) next = _counterAt;
            if (next <= _now) throw new InvalidOperationException("预测事件时间未前进。");
            _now = next;
        }
    }

    private CellBits Explode()
    {
        bool[] pending = _pending;
        if (_count > pending.Length)
        {
            if (_largePending is null || _largePending.Length < _count)
            {
                if (_largePending is not null) ArrayPool<bool>.Shared.Return(_largePending);
                _largePending = ArrayPool<bool>.Shared.Rent(_count);
            }
            pending = _largePending;
        }
        pending.AsSpan(0, _count).Clear();
        bool any = false;
        for (int i = 0; i < _count; i++) if (_bubbles[i].Alive && _bubbles[i].Due <= _now) { pending[i] = true; any = true; }
        if (!any) return default;
        CellBits coverage = default;
        // 泡泡数可超过位图容量，处理标记直接在 Alive 中保存；地形直到整个连锁批次结束才改动。
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < _count; i++) if (pending[i] && _bubbles[i].Alive)
            {
                ref var b = ref _bubbles[i]; b.Alive = false; b.Exploded = _now; changed = true;
                coverage.Add(b.Position.Index);
                int range = b.Owner == _owner && _now > _rootEnd ? _futureRange : b.Range;
                for (int d = 1; d <= 4; d++)
                {
                    var p = b.Position;
                    for (int k = 0; k < Math.Min(range, 21); k++)
                    {
                        p = p.Next((Direction)d); if (!p.InBounds || _terrain[p.Index] == Cell.Wall) break;
                        coverage.Add(p.Index); if (_terrain[p.Index] == Cell.Crate) break;
                    }
                }
                for (int j = 0; j < _count; j++) if (_bubbles[j].Alive && coverage.Has(_bubbles[j].Position.Index)) pending[j] = true;
            }
        } while (changed);
        for (int i = 0; i < 441; i++) if (coverage.Has(i) && _terrain[i] == Cell.Crate)
        {
            _terrain[i] = Cell.Empty; _cleared.Add(i); _clearedAt[i] = _now;
        }
        for (int i = 0; i < 441; i++) if (coverage.Has(i) && _memory.Items[i] != ItemType.None) _picked.Add(i);
        if (_blastCount == _blasts.Length)
        {
            var next = ArrayPool<BlastBatch>.Shared.Rent(checked(_blastCount * 2));
            _blasts.AsSpan(0, _blastCount).CopyTo(next); ArrayPool<BlastBatch>.Shared.Return(_blasts); _blasts = next;
        }
        _blasts[_blastCount++] = new(_now, coverage);
        return coverage;
    }

    public bool Blocked(Position p, RationalTime at)
    {
        if (!p.InBounds || _memory.Terrain[p.Index] == Cell.Wall) return true;
        if (_memory.Terrain[p.Index] == Cell.Crate && (!_cleared.Has(p.Index) || _clearedAt[p.Index] >= at)) return true;
        foreach (var b in Bubbles)
            if (b.Position == p && b.Placed < at && (b.Alive || b.Exploded >= at)) return true;
        return false;
    }
    private bool Hit(Position position, RationalTime after, RationalTime through)
    {
        foreach (var b in Blasts) if (b.Time > after && b.Time <= through && b.Coverage.Has(position.Index)) return true;
        return false;
    }

    /// <summary>未来一轮固定方向，不产生新泡泡；跨线成功当刻改归属，同刻移除仍阻挡。</summary>
    public MotionResult Move(Position position, int speed, Direction direction, RationalTime start)
    {
        var end = start + 1;
        if (direction == Direction.Wait) return new(!Hit(position, start, end), position, default, 0, 0, 0);
        var at = start;
        for (int step = 0; step < Math.Min(speed, 22); step++)
        {
            var next = position.Next(direction); var half = start + new RationalTime(2L * step + 1, 2L * speed);
            var center = start + new RationalTime(step + 1, speed);
            // 在半程之前归旧格；在成功半程当刻归新格，所以用严格界限排除同刻旧格伤害。
            foreach (var b in Blasts) if (b.Time > at && b.Time < half && b.Coverage.Has(position.Index)) return new(false, position, b.Time, 0, 0, 0);
            if (Blocked(next, half)) return new(!Hit(position, at, end), position, default, 0, 0, 0);
            position = next;
            foreach (var b in Blasts) if (b.Time >= half && b.Time <= center && b.Coverage.Has(position.Index)) return new(false, position, b.Time, 0, 0, 0);
            at = center;
            if (at == end) return new(true, position, default, 0, 0, 0);
        }
        return new(!Hit(position, at, end), position, default, 0, 0, 0);
    }
    public bool UnsafeFutureAttributePickup(Position from, Direction direction, int speed, RationalTime start)
    {
        var p = from;
        for (int step = 0; step <= Math.Min(speed, 21); step++)
        {
            // 不能把预测中未来才被炸掉的道具，当成现在已经不存在。
            if (_memory.Items[p.Index] == ItemType.Speed && !_rootRemovedItems.Has(p.Index)) return true;
            if (_memory.Items[p.Index] == ItemType.Range && !_rootRemovedItems.Has(p.Index))
                foreach (var b in Bubbles) if (b.Owner == _owner && (b.Alive || b.Exploded > start + new RationalTime(step, speed))) return true;
            if (direction == Direction.Wait) break;
            var next = p.Next(direction);
            if (Blocked(next, start + new RationalTime(2L * step + 1, 2L * speed))) break;
            p = next;
        }
        return false;
    }
    public void RememberPlacements(WorldMemory memory)
    {
        foreach (var b in Bubbles) if (b.Root && b.Due > _rootEnd)
            memory.AddBubble(new TrackedBubble { Position = b.Position, Owner = memory.Self.Id, Range = b.Range, Due = b.Due,
                ObservedRound = memory.Round, Known = true, Estimated = true });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ArrayPool<Cell>.Shared.Return(_terrain); ArrayPool<RationalTime>.Shared.Return(_clearedAt);
        ArrayPool<ForecastBubble>.Shared.Return(_bubbles); ArrayPool<BlastBatch>.Shared.Return(_blasts);
        ArrayPool<bool>.Shared.Return(_pending); if (_largePending is not null) ArrayPool<bool>.Shared.Return(_largePending);
    }
}
