using System.Buffers;
using System.Text.Json;

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
    private bool _disposed;
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
        if (plans.Length != 4) throw new InvalidOperationException("需要四人完整计划；已死亡玩家的动作只记录并忽略。");
        // 先完整校验，防止一半计划已生效才发现后续输入错误。
        for (int i = 0; i < 4; i++)
        {
            var plan = plans[i];
            var start = _players[i].Position;
            if (plan.PlayerId != i + 1 || !Enum.IsDefined(plan.Direction)) throw new InvalidDataException("计划玩家或方向不合法。");
            if (!_players[i].Alive) continue;
            if (plan.Placement is { } p && p != start && p != start.Next(plan.Direction))
                throw new InvalidDataException("放置点不属于本轮路径。");
        }
        _eventCount = _moveCount = 0;
        for (int i = 0; i < 4; i++)
            if (!_players[i].Alive)
                Add(new(EventKind.ActionIgnored, Tick, i + 1, 0, _players[i].Position, _players[i].Position, false, Tick + 1, SkipReason.Dead));
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
            if (!_players[i].Alive && _players[i].DeathTick != Tick) continue;
            if (plan.Placement is not { } target || (originPlacements & (1 << i)) != 0) continue;
            if (_players[i].Hit || _players[i].Position != target)
                Add(new(EventKind.PlacementSkipped, Tick, i + 1, 0, target, target, false, Reason: _players[i].Alive ? SkipReason.NotReached : SkipReason.Dead));
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
            Add(new(EventKind.PlacementSkipped, time, playerIndex + 1, 0, position, position, false, Reason: SkipReason.Capacity));
            return;
        }
        var bubble = new Bubble(++_nextBubbleId, playerIndex + 1, position, time, time + 3);
        _bubbles[_bubbleCount++] = bubble;
        _placed++;
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
            // 先清理本批已爆炸泡泡，再记录死亡时仍在场的自有泡泡。
            int retained = 0;
            for (int b = 0; b < _bubbleCount; b++)
                if (_bubbles[b].DueAt > Tick) _bubbles[retained++] = _bubbles[b];
            _bubbleCount = retained;
            for (int i = 0; i < 4; i++)
            {
                ref var player = ref _players[i];
                if (!player.Alive) continue; // 后续爆炸不重复淘汰或产生第二个死亡事件。
                bool safe = !covered[player.Position.Index];
                Add(new(EventKind.Safety, Tick, player.Id, 0, player.Position, player.Position, safe));
                if (safe) continue;
                player.Hit = true;
                player.DeathTick = Tick;
                int owned = 0;
                for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].OwnerId == player.Id) owned++;
                player.OwnedBubblesAtDeath = owned;
                // 只取消动作，保留已放泡泡及归属，让它们继续按原到期时间爆炸。
                Add(new(EventKind.Death, Tick, player.Id, 0, player.Position, player.Position, false, owned));
            }
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

    /// <summary>按脚本独立声明的结果验收，死亡场景预期死亡也可以验证通过。</summary>
    public ValidationResult Validate(JsonElement expected, ReadOnlySpan<SimulationEvent> history)
    {
        int safe = 0, walls = 0, expectedDeaths = 0, deaths = 0, deadSkips = 0, ignored = 0;
        bool playersMatch = true, mapMatches = true, bubblesMatch = true, actionsMatch = true;
        var expectedPlayers = expected.GetProperty("players");
        for (int i = 0; i < 4; i++)
        {
            var player = _players[i];
            if (player.Alive) safe++;
            var row = expectedPlayers[i];
            var deathJson = row.GetProperty("deathTick");
            int? death = deathJson.ValueKind == JsonValueKind.Null ? null : deathJson.GetInt32();
            if (death is not null) expectedDeaths++;
            int deathEvents = 0;
            foreach (var e in history)
                if (e.Kind == EventKind.Death && e.PlayerId == player.Id)
                {
                    deathEvents++;
                    playersMatch &= e.Tick == death && e.Position == player.Position && e.Value == row.GetProperty("ownedBubblesAtDeath").GetInt32();
                }
            playersMatch &= deathEvents == (death is null ? 0 : 1);
            playersMatch &= player.Position == ScenarioScript.ReadPosition(row.GetProperty("position")) &&
                player.DeathTick == death && player.Alive == (death is null) &&
                player.OwnedBubblesAtDeath == row.GetProperty("ownedBubblesAtDeath").GetInt32();
        }
        foreach (var e in history)
        {
            if (e.Kind == EventKind.Death) deaths++;
            if (e.Kind == EventKind.ActionIgnored) ignored++;
            if (e.Kind == EventKind.PlacementSkipped && e.Reason == SkipReason.Dead) deadSkips++;
            if (e.PlayerId is >= 1 and <= 4 && _players[e.PlayerId - 1].DeathTick is { } diedAt)
                if ((e.Kind == EventKind.Move && e.Tick > diedAt) || (e.Kind == EventKind.Placed && e.Tick >= diedAt)) actionsMatch = false;
        }
        playersMatch &= deaths == expectedDeaths;
        actionsMatch &= deadSkips == expected.GetProperty("deadPlacementSkips").GetInt32() && ignored == expected.GetProperty("ignoredDeadActions").GetInt32();

        var expectedBubbles = expected.GetProperty("bubbles");
        foreach (var row in expectedBubbles.EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32(), owner = row.GetProperty("ownerId").GetInt32();
            var position = ScenarioScript.ReadPosition(row.GetProperty("position"));
            int placedAt = row.GetProperty("placedAt").GetInt32(), explodedAt = row.GetProperty("explodedAt").GetInt32();
            int placedMatches = 0, explodedMatches = 0;
            foreach (var e in history)
            {
                if (e.BubbleId != id || e.PlayerId != owner || e.Position != position) continue;
                if (e.Kind == EventKind.Placed && e.Tick == placedAt) placedMatches++;
                if (e.Kind == EventKind.Exploded && e.Tick == explodedAt) explodedMatches++;
            }
            bubblesMatch &= placedMatches == 1 && explodedMatches == 1;
        }
        bubblesMatch &= _placed == expectedBubbles.GetArrayLength() && _exploded == expectedBubbles.GetArrayLength() && _bubbleCount == 0;

        int expectedDestroyed = expected.GetProperty("destroyedCrates").GetArrayLength();
        var destroyedMask = ArrayPool<bool>.Shared.Rent(CellCount);
        try
        {
            destroyedMask.AsSpan(0, CellCount).Clear();
            foreach (var json in expected.GetProperty("destroyedCrates").EnumerateArray())
                destroyedMask[ScenarioScript.ReadPosition(json).Index] = true;
            for (int i = 0; i < CellCount; i++)
            {
                var p = new Position(i % Size, i / Size);
                var cell = destroyedMask[i] ? Cell.Empty : InitialCell(p);
                mapMatches &= _terrain[i] == cell;
                if (_terrain[i] == Cell.Wall) walls++;
            }
        }
        finally { ArrayPool<bool>.Shared.Return(destroyedMask); }
        mapMatches &= CrateCount == 329 - expectedDestroyed && _destroyed == expectedDestroyed && walls == 100;
        bool passed = playersMatch && mapMatches && bubblesMatch && actionsMatch;
        return new(passed, Tick, CrateCount, walls, _bubbleCount, safe, _placed, _exploded, _destroyed, mapMatches,
            4 - safe, deaths, playersMatch, bubblesMatch, actionsMatch);
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
