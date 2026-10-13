using System.Buffers;

namespace QQT.Core;

/// <summary>受限 M0：一格/tick、整数到期、无连锁。输出事件由宿主记录和展示。</summary>
public sealed class GameSimulation : IDisposable
{
    public const int Size = 21;
    public const int CellCount = Size * Size;
    private readonly Cell[] _terrain = ArrayPool<Cell>.Shared.Rent(CellCount);
    private readonly Player[] _players = ArrayPool<Player>.Shared.Rent(4);
    private readonly Bubble[] _bubbles = ArrayPool<Bubble>.Shared.Rent(12);
    private readonly Move[] _moves = ArrayPool<Move>.Shared.Rent(4);
    private readonly SimulationEvent[] _events = ArrayPool<SimulationEvent>.Shared.Rent(96);
    private int _eventCount, _moveCount, _bubbleCount, _nextBubbleId, _placed, _exploded, _destroyed;
    private bool _disposed, _placementTimesMatch, _explosionTimesMatch;
    public int Tick { get; private set; }
    public int CrateCount { get; private set; }
    public int BubbleCount => _bubbleCount;
    public ReadOnlySpan<Cell> Terrain => _terrain.AsSpan(0, CellCount);
    public ReadOnlySpan<Player> Players => _players.AsSpan(0, 4);
    public ReadOnlySpan<Bubble> Bubbles => _bubbles.AsSpan(0, _bubbleCount);
    public ReadOnlySpan<Move> Moves => _moves.AsSpan(0, _moveCount);
    public ReadOnlySpan<SimulationEvent> Events => _events.AsSpan(0, _eventCount);

    public GameSimulation() => Reset();

    public static Position Spawn(int i) => i switch
    {
        0 => new(0, 0), 1 => new(20, 0), 2 => new(0, 20), 3 => new(20, 20),
        _ => throw new ArgumentOutOfRangeException(nameof(i))
    };
    public static Position SafePoint(int i) => Spawn(i).Next(i < 2 ? Direction.Down : Direction.Up);

    private static Cell InitialCell(Position p)
    {
        if (p.X % 2 == 1 && p.Y % 2 == 1) return Cell.Wall;
        for (int i = 0; i < 4; i++)
        {
            var spawn = Spawn(i);
            if (Math.Abs(p.X - spawn.X) + Math.Abs(p.Y - spawn.Y) <= 1) return Cell.Empty;
        }
        return Cell.Crate;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Tick = _eventCount = _moveCount = _bubbleCount = _nextBubbleId = _placed = _exploded = _destroyed = 0;
        CrateCount = 0;
        _placementTimesMatch = _explosionTimesMatch = true;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = new Position(x, y);
                _terrain[p.Index] = InitialCell(p);
                if (_terrain[p.Index] == Cell.Crate) CrateCount++;
            }
        for (int i = 0; i < 4; i++) _players[i] = new Player { Id = i + 1, Position = Spawn(i) };
    }

    public void Advance(ReadOnlySpan<PlayerAction> plans)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (plans.Length != 4 || Tick >= 4) throw new InvalidOperationException("M0 只能按四人完整计划执行四轮。");
        // 先完整校验，防止一半计划已生效才发现后续输入错误。
        for (int i = 0; i < 4; i++)
        {
            var plan = plans[i];
            var start = _players[i].Position;
            if (plan.PlayerId != i + 1 || !Enum.IsDefined(plan.Direction)) throw new InvalidDataException("计划玩家或方向不合法。");
            if (plan.Placement is { } p && p != start && p != start.Next(plan.Direction))
                throw new InvalidDataException("放置点不属于本轮路径。");
        }
        _eventCount = _moveCount = 0;
        int originPlacements = 0;
        for (int i = 0; i < 4; i++)
            if (!_players[i].Hit && plans[i].Placement == _players[i].Position)
            {
                originPlacements |= 1 << i;
                Place(i, _players[i].Position, Tick);
            }

        for (int i = 0; i < 4; i++)
        {
            ref var player = ref _players[i];
            if (player.Hit) continue;
            var from = player.Position;
            var to = from.Next(plans[i].Direction);
            if (to == from) continue;
            // 只检查目标格；站在自己的泡泡格不妨碍向空格离开。
            if (!to.InBounds || _terrain[to.Index] != Cell.Empty || HasBubble(to))
                Add(new(EventKind.Blocked, Tick, player.Id, 0, to, from, false));
            else
            {
                player.Position = to;
                _moves[_moveCount++] = new(player.Id, from, to);
                Add(new(EventKind.Move, Tick + 1, player.Id, 0, to, from, true));
            }
        }
        Tick++;
        ExplodeDue();
        // 终点旧泡泡先引爆和受击，存活者才执行终点放置。
        for (int i = 0; i < 4; i++)
        {
            var plan = plans[i];
            // 已尝试的起点放置不在终点重复执行（包括容量不足的请求）。
            if (plan.Placement is not { } target || (originPlacements & (1 << i)) != 0) continue;
            if (_players[i].Hit || _players[i].Position != target)
                Add(new(EventKind.PlacementSkipped, Tick, i + 1, 0, target, target, false));
            else Place(i, target, Tick);
        }

    }

    private bool HasBubble(Position position)
    {
        for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].Position == position) return true;
        return false;
    }

    private void Place(int playerIndex, Position position, int time)
    {
        int owned = 0;
        for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].OwnerId == playerIndex + 1) owned++;
        if (owned >= 3)
        {
            Add(new(EventKind.PlacementSkipped, time, playerIndex + 1, 0, position, position, false));
            return;
        }
        var bubble = new Bubble(++_nextBubbleId, playerIndex + 1, position, time, time + 3);
        _bubbles[_bubbleCount++] = bubble;
        _placed++;
        _placementTimesMatch &= time == 1;
        Add(new(EventKind.Placed, time, playerIndex + 1, bubble.Id, position, position, true));
    }

    private void ExplodeDue()
    {
        bool any = false;
        for (int b = 0; b < _bubbleCount; b++) any |= _bubbles[b].DueAt <= Tick;
        if (!any) return;
        // 所有覆盖先按旧地图合并；池化局部掩码在消费完毕后归还。
        var covered = ArrayPool<bool>.Shared.Rent(CellCount);
        try
        {
            covered.AsSpan(0, CellCount).Clear();
            for (int b = 0; b < _bubbleCount; b++)
            {
                var bubble = _bubbles[b];
                if (bubble.DueAt > Tick) continue;
                _exploded++;
                _explosionTimesMatch &= Tick == 4;
                Add(new(EventKind.Exploded, Tick, bubble.OwnerId, bubble.Id, bubble.Position, bubble.Position, true));
                Cover(bubble.Position, bubble, covered);
                for (Direction d = Direction.Up; d <= Direction.Right; d++)
                {
                    var p = bubble.Position.Next(d);
                    if (!p.InBounds || _terrain[p.Index] == Cell.Wall) continue;
                    Cover(p, bubble, covered);
                }
            }
            for (int i = 0; i < CellCount; i++)
                if (covered[i] && _terrain[i] == Cell.Crate)
                {
                    _terrain[i] = Cell.Empty;
                    CrateCount--;
                    _destroyed++;
                    var p = new Position(i % Size, i / Size);
                    Add(new(EventKind.CrateDestroyed, Tick, 0, 0, p, p, true));
                }
            for (int i = 0; i < 4; i++)
            {
                ref var player = ref _players[i];
                bool safe = !covered[player.Position.Index];
                player.Hit |= !safe;
                Add(new(EventKind.Safety, Tick, player.Id, 0, player.Position, player.Position, safe));
            }
            int retained = 0;
            for (int b = 0; b < _bubbleCount; b++)
                if (_bubbles[b].DueAt > Tick) _bubbles[retained++] = _bubbles[b];
            _bubbleCount = retained;
        }
        finally { ArrayPool<bool>.Shared.Return(covered); }
    }

    private void Cover(Position position, Bubble bubble, bool[] mask)
    {
        mask[position.Index] = true;
        Add(new(EventKind.Covered, Tick, bubble.OwnerId, bubble.Id, position, position, true));
    }

    private void Add(SimulationEvent item)
    {
        if (_eventCount >= 96) throw new InvalidOperationException("M0 事件缓冲超出场景容量。");
        _events[_eventCount++] = item;
    }

    public ValidationResult Validate()
    {
        int safe = 0, walls = 0;
        bool positions = true, mapMatches = true;
        for (int i = 0; i < 4; i++)
        {
            if (!_players[i].Hit) safe++;
            positions &= _players[i].Position == SafePoint(i);
        }
        for (int i = 0; i < CellCount; i++)
        {
            var p = new Position(i % Size, i / Size);
            bool expectedDestroyed = (p.X == 2 || p.X == 18) && (p.Y == 0 || p.Y == 20);
            var expected = expectedDestroyed ? Cell.Empty : InitialCell(p);
            mapMatches &= _terrain[i] == expected;
            if (_terrain[i] == Cell.Wall) walls++;
        }
        bool passed = Tick == 4 && safe == 4 && positions && mapMatches && CrateCount == 325 && walls == 100 &&
            _bubbleCount == 0 && _placed == 4 && _exploded == 4 && _destroyed == 4 && _placementTimesMatch && _explosionTimesMatch;
        return new(passed, Tick, CrateCount, walls, _bubbleCount, safe, _placed, _exploded, _destroyed, mapMatches);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<Cell>.Shared.Return(_terrain);
        ArrayPool<Player>.Shared.Return(_players);
        ArrayPool<Bubble>.Shared.Return(_bubbles);
        ArrayPool<Move>.Shared.Return(_moves);
        ArrayPool<SimulationEvent>.Shared.Return(_events);
    }
}
