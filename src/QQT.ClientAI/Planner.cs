using System.Buffers;
using QQT.Core;

namespace QQT.ClientAI;

public readonly record struct Decision(Plan Plan, double Score, int EscapeDepth, int Horizon, int Candidates, int Nodes, int Placed, int Crates, bool CounterRisk, Position Endpoint);
internal readonly record struct SearchNode(Position Position, double Score) : IComparable<SearchNode>
{
    public int CompareTo(SearchNode other) => other.Score.CompareTo(Score);
}
internal readonly record struct Site(int Step, double Value) : IComparable<Site>
{
    public int CompareTo(Site other) => other.Value.CompareTo(Value);
}
internal readonly record struct EscapeResult(int Depth, double Score, int Count);
internal readonly record struct CounterSite(int Owner, Position Position, RationalTime At, double Value) : IComparable<CounterSite>
{
    public int CompareTo(CounterSite other) => other.Value.CompareTo(Value);
}

/// <summary>有限节点的滚动搜索：先检查已知事件及反击情景，再比较开路、增益和对手逃生空间。</summary>
public sealed class Planner : IDisposable
{
    private const int Width = 24;
    private readonly Forecast _forecast = new(), _counter = new();
    private readonly Plan[] _plans = ArrayPool<Plan>.Shared.Rent(256);
    private readonly Site[] _sites = ArrayPool<Site>.Shared.Rent(21);
    private readonly SearchNode[] _front = ArrayPool<SearchNode>.Shared.Rent(Width);
    private readonly SearchNode[] _next = ArrayPool<SearchNode>.Shared.Rent(Width * 5);
    private readonly double[] _bestCell = ArrayPool<double>.Shared.Rent(441);
    private readonly int[] _distance = ArrayPool<int>.Shared.Rent(441);
    private readonly int[] _queue = ArrayPool<int>.Shared.Rent(441);
    private readonly bool[] _queued = ArrayPool<bool>.Shared.Rent(441);
    private readonly int[] _enemyBaseline = ArrayPool<int>.Shared.Rent(5);
    private readonly CounterSite[] _counterSites = ArrayPool<CounterSite>.Shared.Rent(256);
    private readonly int _seed, _nodeBudget;
    private WorldMemory _memory = null!;
    private int _nodes, _horizon;
    private bool _disposed;
    public Planner(int seed, int nodeBudget) { _seed = seed; _nodeBudget = nodeBudget; }

    public Decision Choose(WorldMemory memory, AiLog log, CancellationToken token)
    {
        _memory = memory; _nodes = 0;
        _horizon = Math.Clamp((int)Math.Ceiling(memory.Self.Lifetime.ToDouble()) + 2, 5, 16);
        BuildGoalField(); int count = Generate();
        _forecast.Build(memory, new(Direction.Wait, 0), _horizon);
        var baselineCrates = _forecast.Destroyed;
        _enemyBaseline.AsSpan(0, 5).Clear();
        for (int i = 1; i <= 4; i++)
        {
            ref var e = ref memory.Enemies[i]; int distance = WorldMemory.Distance(e.Position, memory.Self.Position);
            if (!e.Alive || !e.Located || e.PositionRound != memory.Round || distance > memory.Self.Range + e.Speed * 4 + 5) continue;
            _enemyBaseline[i] = EnemyReachable(_forecast, e);
        }
        Decision best = new(new(Direction.Wait, 0), double.NegativeInfinity, 0, _horizon, count, 0, 0, 0, false, memory.Self.Position);
        int evaluated = 0;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            // 五个无放置方向总会评估；预算只限制额外进攻分支，不让客户端不交计划。
            if (i >= 5 && _nodes >= _nodeBudget) break;
            evaluated++;
            var plan = _plans[i]; _forecast.Build(memory, plan, _horizon);
            var motion = _forecast.RootMotion;
            var escape = motion.Alive ? Escape(_forecast, motion, token) : new EscapeResult(0, 0, 0);
            int crates = _forecast.Destroyed.ExceptCount(baselineCrates);
            double attack = 0;
            if (plan.Count > 0 && escape.Depth == _horizon)
                for (int enemy = 1; enemy <= 4; enemy++) if (_enemyBaseline[enemy] > 0)
                {
                    int remaining = EnemyReachable(_forecast, memory.Enemies[enemy]);
                    attack += remaining == 0 ? 240 : 28.0 * (_enemyBaseline[enemy] - remaining) / _enemyBaseline[enemy];
                }
            bool counterRisk = false;
            double counterPenalty = 0;
            int counterCount = escape.Depth > 0 ? CounterSites(_forecast) : 0;
            for (int counterIndex = 0; counterIndex < Math.Min(counterCount, 4); counterIndex++)
            {
                var site = _counterSites[counterIndex];
                _counter.Build(memory, plan, _horizon, site.Owner, site.Position, site.At);
                var alternate = _counter.RootMotion.Alive ? Escape(_counter, _counter.RootMotion, token) : new EscapeResult(0, 0, 0);
                if (alternate.Depth < _horizon) { counterRisk = true; counterPenalty = Math.Max(counterPenalty, 1800 + (_horizon - alternate.Depth) * 250); }
            }
            double uncertainty = Uncertainty(motion.Position);
            // 不能只奖励若干轮后的最佳位置，否则每轮都把有用的第一步推迟到“下一轮”。
            // 当前实际推进、当前访问次数及明确阻挡同样进入评价，抑制角落循环和撞墙等待。
            double progress = (_distance[memory.Self.Position.Index] - _distance[motion.Position.Index]) * 1.6;
            double score = escape.Score + PositionValue(motion.Position) * 0.65 + progress + crates * 13 + motion.Pickups * 22 + attack - _forecast.RootPlaced * 1.4 - counterPenalty - uncertainty;
            if (plan.Direction != Direction.Wait && motion.Position == memory.Self.Position) score -= 2.5;
            if (plan.Direction == Direction.Wait) score -= 0.45;
            if (plan.Count > 0 && _forecast.RootPlaced == 0) score -= 1;
            if (escape.Depth < _horizon) score -= 100000 + (_horizon - escape.Depth) * 3000;
            score += Jitter(plan);
            log.Candidate(memory.Round, plan, score, escape.Depth, crates, attack, uncertainty, counterRisk);
            if (score > best.Score) best = new(plan, score, escape.Depth, _horizon, i + 1, _nodes, _forecast.RootPlaced, crates, counterRisk, motion.Position);
        }
        return best with { Nodes = _nodes, Candidates = evaluated };
    }

    public void Commit(WorldMemory memory, in Decision decision)
    {
        _forecast.Build(memory, decision.Plan, decision.Horizon); _forecast.RememberPlacements(memory);
        memory.PreviousDirection = decision.Plan.Direction;
    }

    private EscapeResult Escape(Forecast forecast, MotionResult root, CancellationToken token)
    {
        int count = 1, reached = 1; _front[0] = new(root.Position, PositionValue(root.Position));
        double best = _front[0].Score;
        int speed = _memory.Self.Speed + root.SpeedGain;
        for (int depth = 1; depth < _horizon; depth++)
        {
            token.ThrowIfCancellationRequested(); _bestCell.AsSpan(0, 441).Fill(double.NegativeInfinity);
            int nextCount = 0; var at = _memory.Time + depth;
            for (int i = 0; i < count; i++) for (int d = 0; d <= 4; d++)
            {
                _nodes++;
                // 后续火力拾取会改变既有泡泡；在尚有自身泡泡时，不用固定范围推演证明这条路线安全。
                if (forecast.UnsafeFutureAttributePickup(_front[i].Position, (Direction)d, speed, at)) continue;
                var move = forecast.Move(_front[i].Position, speed, (Direction)d, at);
                if (!move.Alive) continue;
                double value = PositionValue(move.Position) + _front[i].Score * 0.08 - (d == 0 ? 0.08 : 0);
                if (value <= _bestCell[move.Position.Index]) continue;
                _bestCell[move.Position.Index] = value;
                _next[nextCount++] = new(move.Position, value);
            }
            if (nextCount == 0) return new(reached, best, 0);
            _next.AsSpan(0, nextCount).Sort();
            CellBits included = default; count = 0;
            for (int i = 0; i < nextCount && count < Width; i++) if (!included.Has(_next[i].Position.Index))
            { included.Add(_next[i].Position.Index); _front[count++] = _next[i]; }
            best = _front[0].Score; reached++;
        }
        return new(reached, best + Math.Min(count, 12) * 0.2, count);
    }

    private int EnemyReachable(Forecast forecast, EnemyState enemy)
    {
        CellBits current = default; current.Add(enemy.Position.Index);
        for (int depth = 0; depth < _horizon; depth++)
        {
            CellBits next = default;
            for (int i = 0; i < 441; i++) if (current.Has(i)) for (int d = 0; d <= 4; d++)
            {
                var move = forecast.Move(new(i % 21, i / 21), enemy.Speed, (Direction)d, _memory.Time + depth);
                if (move.Alive) next.Add(move.Position.Index);
            }
            current = next; if (current.Count == 0) break;
        }
        return current.Count;
    }

    private int CounterSites(Forecast baseline)
    {
        int count = 0;
        for (int i = 1; i <= 4; i++)
        {
            var e = _memory.Enemies[i];
            if (!e.Alive || !e.Located || e.PositionRound != _memory.Round || _memory.Round - e.DetailRound > 1 ||
                WorldMemory.Distance(e.Position, _memory.Self.Position) > _memory.Self.Range + e.Range + e.Speed * 2 + 3) continue;
            AddCounterSite(i, e.Position, _memory.Time, baseline, ref count);
            for (int d = 1; d <= 4; d++)
            {
                var move = baseline.Move(e.Position, e.Speed, (Direction)d, _memory.Time);
                if (!move.Alive) continue;
                int distance = WorldMemory.Distance(e.Position, move.Position);
                var p = e.Position;
                for (int step = 1; step <= distance; step++)
                { p = p.Next((Direction)d); AddCounterSite(i, p, _memory.Time + new RationalTime(step, e.Speed), baseline, ref count); }
            }
        }
        _counterSites.AsSpan(0, count).Sort(); return count;
    }
    private void AddCounterSite(int owner, Position position, RationalTime at, Forecast baseline, ref int count)
    {
        if (count >= _counterSites.Length) return;
        int distance = WorldMemory.Distance(position, baseline.RootMotion.Position);
        double value = 20 - distance * 2;
        if (position.X == baseline.RootMotion.Position.X || position.Y == baseline.RootMotion.Position.Y) value += 4;
        foreach (var b in baseline.Bubbles)
            if (b.Position == position && (b.Alive || b.Exploded >= at)) return;
        foreach (var b in baseline.Bubbles)
            if (b.Exploded > at && b.Exploded <= _memory.Time + 2 && WorldMemory.Distance(position, b.Position) <= b.Range &&
                (position.X == b.Position.X || position.Y == b.Position.Y)) value += 8;
        _counterSites[count++] = new(owner, position, at, value);
    }

    private int Generate()
    {
        int count = 0;
        for (int d = 0; d <= 4; d++) _plans[count++] = new((Direction)d, 0);
        for (int d = 0; d <= 4; d++)
        {
            int sites = 0; var direction = (Direction)d;
            for (int step = 0; step <= Math.Min(_memory.Self.Speed, 20); step++)
            {
                if (d == 0 && step > 0) break;
                var point = new Plan(direction, 0).At(_memory.Self.Position, step);
                if (!point.InBounds) break;
                if (_memory.Terrain[point.Index] != Cell.Empty && step > 0) break;
                double value = SiteValue(point);
                if (value > 0.2) _sites[sites++] = new(step, value);
            }
            _sites.AsSpan(0, sites).Sort();
            for (int i = 0; i < Math.Min(sites, 8); i++) _plans[count++] = new(direction, 1U << _sites[i].Step);
            if (sites >= 2) _plans[count++] = new(direction, (1U << _sites[0].Step) | (1U << _sites[1].Step));
            if (sites >= 3 && _memory.Self.Capacity >= 3)
                _plans[count++] = new(direction, (1U << _sites[0].Step) | (1U << _sites[1].Step) | (1U << _sites[2].Step));
        }
        return count;
    }
    private double SiteValue(Position point)
    {
        foreach (var b in _memory.Bubbles) if (b.Position == point && b.Owner == _memory.Self.Id) return 0;
        double score = 0;
        for (int d = 1; d <= 4; d++)
        {
            var p = point;
            for (int k = 0; k < Math.Min(_memory.Self.Range, 21); k++)
            {
                p = p.Next((Direction)d); if (!p.InBounds || _memory.Terrain[p.Index] == Cell.Wall) break;
                if (_memory.Terrain[p.Index] == Cell.Crate) { score += _memory.Seen[p.Index] == _memory.Round ? 3 : 1.5; break; }
            }
        }
        for (int i = 1; i <= 4; i++)
        {
            ref var e = ref _memory.Enemies[i];
            if (e.Alive && e.Located && _memory.Round - e.PositionRound <= 2 && WorldMemory.Distance(point, e.Position) <= _memory.Self.Range + e.Speed * 3)
                score += 3.0 / (1 + WorldMemory.Distance(point, e.Position));
        }
        return score;
    }

    private void BuildGoalField()
    {
        Position goal = _memory.Self.Position; double goalScore = double.NegativeInfinity;
        for (int i = 0; i < 441; i++) if (_memory.Items[i] != ItemType.None)
        {
            var p = new Position(i % 21, i / 21);
            double value = 28 - WorldMemory.Distance(p, _memory.Self.Position) * 1.3;
            if (_memory.Items[i] == ItemType.Speed && _memory.Self.Speed <= 2) value += 8;
            if (_memory.Items[i] == ItemType.Range && _memory.Self.Range <= 2) value += 5;
            if (_memory.Round - _memory.Seen[i] > 4) value -= 15;
            if (value > goalScore) { goalScore = value; goal = p; }
        }
        for (int i = 1; i <= 4; i++)
        {
            ref var e = ref _memory.Enemies[i]; if (!e.Alive || !e.Located) continue;
            double value = 12 - WorldMemory.Distance(e.Position, _memory.Self.Position) * 0.15;
            if (value > goalScore) { goalScore = value; goal = e.Position; }
        }
        if (double.IsNegativeInfinity(goalScore)) goal = new(10, 10);
        _distance.AsSpan(0, 441).Fill(100000); _queued.AsSpan(0, 441).Clear();
        int head = 0, tail = 1, count = 1; _queue[0] = goal.Index; _distance[goal.Index] = 0; _queued[goal.Index] = true;
        while (count > 0)
        {
            int i = _queue[head]; head = (head + 1) % 441; count--; _queued[i] = false;
            var p = new Position(i % 21, i / 21);
            for (int d = 1; d <= 4; d++)
            {
                var next = p.Next((Direction)d); if (!next.InBounds || _memory.Terrain[next.Index] == Cell.Wall) continue;
                int cost = _distance[i] + (_memory.Terrain[i] == Cell.Crate ? 7 : 1);
                if (cost >= _distance[next.Index]) continue;
                _distance[next.Index] = cost;
                if (!_queued[next.Index]) { _queue[tail] = next.Index; tail = (tail + 1) % 441; count++; _queued[next.Index] = true; }
            }
        }
    }
    private double PositionValue(Position p)
    {
        double score = -Math.Min(_distance[p.Index], 300) * 0.65 - Math.Log(1 + _memory.Visits[p.Index]) * 1.2;
        int exits = 0;
        for (int d = 1; d <= 4; d++) { var next = p.Next((Direction)d); if (next.InBounds && _memory.Terrain[next.Index] == Cell.Empty) exits++; }
        return score + exits * 0.35;
    }
    private double Uncertainty(Position p)
    {
        double risk = _memory.Seen[p.Index] == _memory.Round ? 0 : 1.5;
        foreach (var b in _memory.Bubbles) if ((!b.Known || b.Estimated) && WorldMemory.Distance(p, b.Position) <= b.Range + _memory.Self.Speed) risk += 4;
        return risk;
    }
    private double Jitter(Plan plan)
    {
        uint state = unchecked((uint)(_seed * 397 ^ _memory.Round * 31 ^ (int)plan.Direction * 1009) ^ plan.Placements * 17);
        state ^= state >> 16; state *= 0x7feb352d; state ^= state >> 15;
        return (state % 1000) / 10000.0;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _forecast.Dispose(); _counter.Dispose(); ArrayPool<Plan>.Shared.Return(_plans); ArrayPool<Site>.Shared.Return(_sites);
        ArrayPool<SearchNode>.Shared.Return(_front); ArrayPool<SearchNode>.Shared.Return(_next); ArrayPool<double>.Shared.Return(_bestCell);
        ArrayPool<int>.Shared.Return(_distance); ArrayPool<int>.Shared.Return(_queue); ArrayPool<bool>.Shared.Return(_queued); ArrayPool<int>.Shared.Return(_enemyBaseline);
        ArrayPool<CounterSite>.Shared.Return(_counterSites);
    }
}
