using System.Buffers;
using System.Text.Json;

namespace QQT.Core;

/// <summary>可配置人数的精确事件模拟：跨线检查、中心放置、原速回退、瞬时爆炸与即时胜负。输出事件由宿主记录和展示。</summary>
public sealed partial class GameSimulation : IDisposable
{
    public const int Size = 21;
    public const int CellCount = Size * Size;
    public const int InitialEventCapacity = 96;
    private readonly Cell[] _terrain = ArrayPool<Cell>.Shared.Rent(CellCount);
    private readonly Player[] _players = ArrayPool<Player>.Shared.Rent(4);
    private Bubble[] _bubbles = ArrayPool<Bubble>.Shared.Rent(12);
    private Move[] _moves = ArrayPool<Move>.Shared.Rent(4);
    private SimulationEvent[] _events = ArrayPool<SimulationEvent>.Shared.Rent(InitialEventCapacity);
    private int _eventCount, _moveCount, _bubbleCount, _nextBubbleId, _placed, _exploded, _destroyed;
    private bool _disposed;
    private readonly Cell[] _initialTerrain = ArrayPool<Cell>.Shared.Rent(CellCount);
    private readonly MovementSegment[] _segments = ArrayPool<MovementSegment>.Shared.Rent(4);
    private readonly bool[] _attempted = ArrayPool<bool>.Shared.Rent(4 * Size);
    private int _initialCrates, _initialWalls;
    public RationalTime Time { get; private set; }
    private struct MovementSegment
    {
        public Position Origin, Target;
        public RationalTime Start;
        public bool Active, Crossed, Returning;
    }
    public const int ExplosionRange = 1;
    public bool Ended { get; private set; }
    public int? WinnerId { get; private set; }
    public RationalTime? EndTick { get; private set; }
    public int Tick { get; private set; }
    public int CrateCount { get; private set; }
    public int BubbleCount => _bubbleCount;
    public ReadOnlySpan<Cell> Terrain => _terrain.AsSpan(0, CellCount);
    public int PlayerCount { get; private set; } = 4;
    public ReadOnlySpan<Player> Players => _players.AsSpan(0, PlayerCount);
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

    public void Reset(int playerCount = 4, int? spawnSeed = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (playerCount is < 2 or > 4) throw new ArgumentOutOfRangeException(nameof(playerCount));
        PlayerCount = playerCount; _executing = false;
        _tickPlans.AsSpan(0, 4).Clear(); _segments.AsSpan(0, 4).Clear();
        Time = 0;
        ResetItems();
        Tick = _eventCount = _moveCount = _bubbleCount = _nextBubbleId = _placed = _exploded = _destroyed = 0;
        Ended = false; WinnerId = null; EndTick = null;
        CrateCount = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = new Position(x, y);
                _terrain[p.Index] = InitialCell(p);
                if (_terrain[p.Index] == Cell.Crate) CrateCount++;
            }
        var corners = ArrayPool<int>.Shared.Rent(4);
        try
        {
            for (int i = 0; i < 4; i++) corners[i] = i;
            if (spawnSeed is { } seed)
            {
                // 固定算法，不依赖运行时 Random 的实现；记录 seed 即可重现出生分配。
                uint state = unchecked((uint)seed);
                for (int i = 3; i > 0; i--)
                {
                    state = unchecked(state * 1664525u + 1013904223u);
                    int j = (int)(state % (uint)(i + 1));
                    (corners[i], corners[j]) = (corners[j], corners[i]);
                }
            }
            for (int i = 0; i < PlayerCount; i++)
                _players[i] = new Player { Id = i + 1, Position = Spawn(corners[i]), Point = ExactPosition.Center(Spawn(corners[i])), Speed = 1, Capacity = 3, Range = 1, Vision = 2, Lifetime = 3 };
        }
        finally { ArrayPool<int>.Shared.Return(corners); }
        SaveInitialTerrain();
    }

    /// <summary>测试环境只改变初始条件，后续使用与正式地图完全相同的模拟路径。</summary>
    public void Configure(JsonElement initial)
    {
        if (Tick != 0 || Time != 0 || _roundPrepared || _itemsConfigured) throw new InvalidOperationException("只能初始化尚未配置道具或推进的世界。");
        if (initial.GetProperty("map").GetString() != "empty") throw new InvalidDataException("测试地图必须是 empty。");
        _terrain.AsSpan(0, CellCount).Clear();
        if (initial.TryGetProperty("walls", out var walls))
            foreach (var cell in walls.EnumerateArray()) _terrain[ScenarioScript.ReadPosition(cell).Index] = Cell.Wall;
        if (initial.TryGetProperty("crates", out var crates))
            foreach (var cell in crates.EnumerateArray())
            {
                var point = ScenarioScript.ReadPosition(cell);
                if (_terrain[point.Index] != Cell.Empty) throw new InvalidDataException("初始障碍不能重叠。");
                _terrain[point.Index] = Cell.Crate;
            }
        var players = initial.GetProperty("players");
        if (players.GetArrayLength() != PlayerCount) throw new InvalidDataException("初始环境必须配置实际参赛人数。");
        int seen = 0;
        foreach (var row in players.EnumerateArray())
        {
            int id = row.GetProperty("playerId").GetInt32();
            if (id < 1 || id > PlayerCount || (seen & (1 << id)) != 0) throw new InvalidDataException("初始玩家编号非法或重复。");
            seen |= 1 << id;
            ref var player = ref _players[id - 1];
            player.Position = ScenarioScript.ReadPosition(row.GetProperty("position"));
            if (_terrain[player.Position.Index] != Cell.Empty) throw new InvalidDataException("出生点不可位于障碍。");
            player.Point = ExactPosition.Center(player.Position);
            player.Speed = Positive(row, "speed", 1); player.Capacity = Positive(row, "capacity", 3); player.Range = Positive(row, "range", 1); player.Vision = Positive(row, "vision", 2);
            player.Lifetime = row.TryGetProperty("lifetime", out var life) ? RationalTime.Read(life) : 3;
            if (player.Lifetime <= 0) throw new InvalidDataException("泡泡寿命必须为正。");
        }
        SaveInitialTerrain();
        if (initial.TryGetProperty("bubbles", out var bubbles))
            foreach (var row in bubbles.EnumerateArray())
            {
                int owner = row.GetProperty("ownerId").GetInt32();
                if (owner < 1 || owner > PlayerCount) throw new InvalidDataException("初始泡泡归属非法。");
                var position = ScenarioScript.ReadPosition(row.GetProperty("position"));
                var placed = RationalTime.Read(row.GetProperty("placedAt"));
                var due = RationalTime.Read(row.GetProperty("dueAt"));
                if (_terrain[position.Index] != Cell.Empty || placed > 0 || due < 0 || due != placed + _players[owner - 1].Lifetime || Owned(owner) >= _players[owner - 1].Capacity)
                    throw new InvalidDataException("初始泡泡位置、容量或寿命关系非法。");
                Grow(ref _bubbles, _bubbleCount, _bubbleCount + 1);
                var bubble = new Bubble(++_nextBubbleId, owner, position, placed, due);
                _bubbles[_bubbleCount++] = bubble; _placed++;
                Add(new(EventKind.InitialBubble, 0, owner, bubble.Id, position, position, true) { RelatedTime = due });
            }
    }
    private static int Positive(JsonElement row, string name, int fallback)
    {
        int value = row.TryGetProperty(name, out var json) ? json.GetInt32() : fallback;
        return value > 0 ? value : throw new InvalidDataException($"{name} 必须为正整数。");
    }
    private void SaveInitialTerrain()
    {
        _terrain.AsSpan(0, CellCount).CopyTo(_initialTerrain);
        _initialCrates = _initialWalls = 0;
        foreach (var cell in Terrain) { if (cell == Cell.Crate) _initialCrates++; if (cell == Cell.Wall) _initialWalls++; }
        CrateCount = _initialCrates;
    }
    private int Owned(int playerId)
    {
        int count = 0; foreach (var b in Bubbles) if (b.OwnerId == playerId) count++;
        return count;
    }

    /// <summary>完整推进的兼容入口；联机宿主使用 BeginTick / AdvanceEvent 在事件批次间接收命令。</summary>
    public void Advance(ReadOnlySpan<PlayerAction> plans)
    {
        BeginTick(plans);
        while (IsExecuting) AdvanceEvent();
    }

    public void ValidatePlan(in PlayerAction plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int i = plan.PlayerId - 1;
        if ((uint)i >= PlayerCount) throw new InvalidDataException("计划玩家非法。");
        if (!Enum.IsDefined(plan.Direction) || plan.PlacementCount > Size) throw new InvalidDataException("计划玩家、方向或放置数量非法。");
        if (!_players[i].Alive) return;
        for (int k = 0; k < plan.PlacementCount; k++)
        {
            var point = plan.PlacementAt(k);
            var start = _players[i].Position;
            int dx = point.X - start.X, dy = point.Y - start.Y;
            long distance = Math.Abs((long)dx) + Math.Abs((long)dy);
            bool onPath = point.InBounds && distance <= _players[i].Speed && (distance == 0 || plan.Direction switch
            {
                Direction.Up => dx == 0 && dy < 0, Direction.Down => dx == 0 && dy > 0,
                Direction.Left => dy == 0 && dx < 0, Direction.Right => dy == 0 && dx > 0, _ => false
            });
            if (!onPath) throw new InvalidDataException("放置点不属于本轮路径。");
            for (int j = 0; j < k; j++) if (plan.PlacementAt(j) == point) throw new InvalidDataException("同轮放置点重复。");
        }
    }

    private void StartSegment(int i, Direction direction, RationalTime end)
    {
        if (!_players[i].Alive || direction == Direction.Wait || Time >= end) return;
        _segments[i] = new MovementSegment { Origin = _players[i].Position, Target = _players[i].Position.Next(direction), Start = Time, Active = true };
    }
    private void AdvancePoints(RationalTime next)
    {
        for (int i = 0; i < PlayerCount; i++)
        {
            ref var p = ref _players[i]; ref var segment = ref _segments[i];
            if (!p.Alive || !segment.Active) continue;
            var distance = (next - segment.Start) * p.Speed;
            if (segment.Returning) distance = 1 - distance;
            var point = new ExactPosition((RationalTime)segment.Origin.X + distance * (segment.Target.X - segment.Origin.X), (RationalTime)segment.Origin.Y + distance * (segment.Target.Y - segment.Origin.Y));
            Grow(ref _moves, _moveCount, _moveCount + 1);
            _moves[_moveCount++] = new(p.Id, p.Point, point, Time, next); p.Point = point;
        }
    }
    private void AttemptCenters(ReadOnlySpan<PlayerAction> plans, bool origin)
    {
        for (int i = 0; i < PlayerCount; i++)
        {
            if (!_players[i].Alive || (!origin && (_segments[i].Active || _segments[i].Returning))) continue;
            for (int k = 0; k < plans[i].PlacementCount; k++)
                if (!_attempted[i * Size + k] && plans[i].PlacementAt(k) == _players[i].Position)
                {
                    _attempted[i * Size + k] = true; Place(i, _players[i].Position, Time);
                }
        }
    }
    private void SkipRemaining(ReadOnlySpan<PlayerAction> plans, bool deadOnly = false)
    {
        for (int i = 0; i < PlayerCount; i++) for (int k = 0; k < plans[i].PlacementCount; k++)
            if (!_attempted[i * Size + k] && (!deadOnly || !_players[i].Alive))
            {
                _attempted[i * Size + k] = true;
                Add(new(EventKind.PlacementSkipped, Time, i + 1, 0, plans[i].PlacementAt(k), _players[i].Position, false, Reason: _players[i].Alive ? SkipReason.NotReached : SkipReason.Dead));
            }
    }
    private void RecordStates()
    {
        foreach (var p in Players) Add(new(EventKind.State, Time, p.Id, 0, p.Position, p.Position, p.Alive, Owned(p.Id)));
    }
    private bool ResolveOutcome()
    {
        int survivors = 0, winner = 0;
        foreach (var p in Players) if (p.Alive) { survivors++; winner = p.Id; }
        if (survivors > 1) return false;
        RecordStates(); Ended = true; EndTick = Time; WinnerId = survivors == 1 ? winner : null;
        Add(new(EventKind.MatchEnded, Time, WinnerId ?? 0, 0, default, default, true, survivors));
        return true;
    }

    private bool HasBubble(Position position)
    {
        for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].Position == position) return true;
        return false;
    }

    private void Place(int playerIndex, Position position, RationalTime time)
    {
        int owned = 0;
        for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].OwnerId == playerIndex + 1) owned++;
        if (owned >= _players[playerIndex].Capacity)
        {
            Add(new(EventKind.PlacementSkipped, time, playerIndex + 1, 0, position, position, false, Reason: SkipReason.Capacity));
            return;
        }
        var bubble = new Bubble(++_nextBubbleId, playerIndex + 1, position, time, time + _players[playerIndex].Lifetime);
        Grow(ref _bubbles, _bubbleCount, _bubbleCount + 1);
        _bubbles[_bubbleCount++] = bubble;
        _placed++;
        Add(new(EventKind.Placed, time, playerIndex + 1, bubble.Id, position, position, true) { RelatedTime = bubble.DueAt });
    }

    private void ExplodeDue()
    {
        bool any = false;
        for (int b = 0; b < _bubbleCount; b++) any |= _bubbles[b].DueAt <= Time;
        if (!any) return;
        // 本方法唯一持有临时缓冲；标记在入队时写入，重复覆盖不能重复引爆或释放容量。
        // 地形与泡泡数组保持不变直到队列完全消费，所有到期和递归连锁共享旧地图。
        var covered = ArrayPool<bool>.Shared.Rent(CellCount);
        bool[]? scheduled = null;
        int[]? pending = null;
        try
        {
            scheduled = ArrayPool<bool>.Shared.Rent(_bubbleCount);
            pending = ArrayPool<int>.Shared.Rent(_bubbleCount);
            covered.AsSpan(0, CellCount).Clear();
            scheduled.AsSpan(0, _bubbleCount).Clear();
            int pendingCount = 0;
            for (int b = 0; b < _bubbleCount; b++)
                if (_bubbles[b].DueAt <= Time)
                {
                    scheduled[b] = true;
                    pending[pendingCount++] = b;
                }
            for (int head = 0; head < pendingCount; head++)
            {
                var bubble = _bubbles[pending[head]];
                _exploded++;
                Add(new(EventKind.Exploded, Time, bubble.OwnerId, bubble.Id, bubble.Position, bubble.Position, true) { RelatedTime = bubble.DueAt });
                TraceBlast(bubble, _players[bubble.OwnerId - 1].Range, covered, scheduled, pending, ref pendingCount);
            }
            DestroyExposedItems(covered); // 清理原有露出道具后才释放箱内道具，整批覆盖不重复销毁新掉落者。
            for (int i = 0; i < CellCount; i++)
                if (covered[i] && _terrain[i] == Cell.Crate)
                {
                    _terrain[i] = Cell.Empty;
                    CrateCount--;
                    _destroyed++;
                    var p = new Position(i % Size, i / Size);
                    Add(new(EventKind.CrateDestroyed, Time, 0, 0, p, p, true));
                    ExposeItem(p);
                }
            // 先清理本批已爆炸泡泡，再记录死亡时仍在场的自有泡泡。
            int retained = 0;
            for (int b = 0; b < _bubbleCount; b++)
                if (!scheduled[b]) _bubbles[retained++] = _bubbles[b];
            _bubbleCount = retained;
            for (int i = 0; i < PlayerCount; i++)
            {
                ref var player = ref _players[i];
                if (!player.Alive) continue; // 后续爆炸不重复淘汰或产生第二个死亡事件。
                bool safe = !covered[player.Position.Index];
                Add(new(EventKind.Safety, Time, player.Id, 0, player.Position, player.Position, safe));
                if (safe) continue;
                player.Hit = true;
                player.DeathTick = Time;
                int owned = 0;
                for (int b = 0; b < _bubbleCount; b++) if (_bubbles[b].OwnerId == player.Id) owned++;
                player.OwnedBubblesAtDeath = owned;
                // 只取消动作，保留已放泡泡及归属，让它们继续按原到期时间爆炸。
                Add(new(EventKind.Death, Time, player.Id, 0, player.Position, player.Position, false, owned));
            }
        }
        finally
        {
            if (pending is not null) ArrayPool<int>.Shared.Return(pending);
            if (scheduled is not null) ArrayPool<bool>.Shared.Return(scheduled);
            ArrayPool<bool>.Shared.Return(covered);
        }
    }

    /// <summary>扫描原泡泡的每条射线；触发泡泡不消耗额外射程，也不阻挡原射线。</summary>
    private void TraceBlast(Bubble bubble, int range, bool[] covered, bool[] scheduled, int[] pending, ref int pendingCount)
    {
        Cover(bubble.Position, bubble, covered, scheduled, pending, ref pendingCount);
        for (Direction d = Direction.Up; d <= Direction.Right; d++)
        {
            var p = bubble.Position;
            for (int distance = 1; distance <= range; distance++)
            {
                p = p.Next(d);
                if (!p.InBounds || _terrain[p.Index] == Cell.Wall) break;
                Cover(p, bubble, covered, scheduled, pending, ref pendingCount);
                if (_terrain[p.Index] == Cell.Crate) break;
            }
        }
    }

    private void Cover(Position position, Bubble bubble, bool[] covered, bool[] scheduled, int[] pending, ref int pendingCount)
    {
        covered[position.Index] = true;
        Add(new(EventKind.Covered, Time, bubble.OwnerId, bubble.Id, position, position, true));
        // 也扫描爆心，以覆盖同格叠放；入队后继续原射线，只有墙和箱子能够截断。
        for (int b = 0; b < _bubbleCount; b++)
            if (!scheduled[b] && _bubbles[b].Position == position)
            {
                scheduled[b] = true;
                pending[pendingCount++] = b;
                var target = _bubbles[b];
                Add(new(EventKind.Chained, Time, target.OwnerId, target.Id, position, bubble.Position, true, bubble.Id));
            }
    }

    // 世界唯一持有这些池数组；扩容仅复制有效区，归还旧数组，快照序列化后不借用数组。
    private static void Grow<T>(ref T[] array, int used, int required)
    {
        if (required <= array.Length) return;
        var next = ArrayPool<T>.Shared.Rent(Math.Max(required, checked(array.Length * 2)));
        array.AsSpan(0, used).CopyTo(next);
        ArrayPool<T>.Shared.Return(array, System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        array = next;
    }
    private void Add(SimulationEvent item)
    {
        Grow(ref _events, _eventCount, _eventCount + 1);
        _events[_eventCount++] = item with { Point = item.PlayerId >= 1 && item.PlayerId <= PlayerCount ? _players[item.PlayerId - 1].Point : default, RoundId = _phase == SimulationPhase.RoundStart ? Tick + 1 : Tick, Phase = _phase,
            Attributes = item.PlayerId >= 1 && item.PlayerId <= PlayerCount ? PlayerAttributes.From(_players[item.PlayerId - 1]) : default,
            Pending = item.PlayerId >= 1 && item.PlayerId <= PlayerCount ? _players[item.PlayerId - 1].Pending : default };
    }

    /// <summary>按脚本独立声明的结果验收，死亡场景预期死亡也可以验证通过。</summary>
    public ValidationResult Validate(JsonElement expected, ReadOnlySpan<SimulationEvent> history)
    {
        int safe = 0, walls = 0, expectedDeaths = 0, deaths = 0, deadSkips = 0, ignored = 0;
        bool playersMatch = true, mapMatches = true, bubblesMatch = true, actionsMatch = true, attributesMatch = true;
        var expectedPlayers = expected.GetProperty("players");
        for (int i = 0; i < PlayerCount; i++)
        {
            var player = _players[i];
            if (player.Alive) safe++;
            var row = expectedPlayers[i];
            attributesMatch &= MatchAttributes(row, PlayerAttributes.From(player), player.Pending);
            if (row.TryGetProperty("point", out var finalPoint)) playersMatch &= player.Point.X == RationalTime.Read(finalPoint.GetProperty("x")) && player.Point.Y == RationalTime.Read(finalPoint.GetProperty("y"));
            var deathJson = row.GetProperty("deathTick");
            RationalTime? death = deathJson.ValueKind == JsonValueKind.Null ? null : RationalTime.Read(deathJson);
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
            if (e.PlayerId >= 1 && e.PlayerId <= PlayerCount && _players[e.PlayerId - 1].DeathTick is { } diedAt)
                if ((e.Kind == EventKind.Move && e.Tick > diedAt) || (e.Kind == EventKind.Placed && e.Tick >= diedAt)) actionsMatch = false;
        }
        playersMatch &= deaths == expectedDeaths;
        actionsMatch &= deadSkips == expected.GetProperty("deadPlacementSkips").GetInt32() && ignored == expected.GetProperty("ignoredDeadActions").GetInt32();

        bool outcomeMatches = !Ended;
        if (expected.TryGetProperty("end", out var end))
        {
            int? winner = end.GetProperty("winnerId").ValueKind == JsonValueKind.Null ? null : end.GetProperty("winnerId").GetInt32();
            outcomeMatches = Ended && WinnerId == winner && EndTick == RationalTime.Read(end.GetProperty("tick")) && Time == EndTick;
        }
        int endings = 0;
        bool endSeen = false;
        foreach (var e in history)
        {
            if (endSeen) { outcomeMatches = false; actionsMatch = false; }
            if (e.Kind != EventKind.MatchEnded) continue;
            endings++; endSeen = true;
            outcomeMatches &= e.Tick == EndTick && e.PlayerId == (WinnerId ?? 0) && e.Value == safe;
        }
        outcomeMatches &= endings == (Ended ? 1 : 0);

        int expectedExploded = 0, expectedRetained = 0;
        var expectedBubbles = expected.GetProperty("bubbles");
        foreach (var row in expectedBubbles.EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32(), owner = row.GetProperty("ownerId").GetInt32();
            var position = ScenarioScript.ReadPosition(row.GetProperty("position"));
            var placedAt = RationalTime.Read(row.GetProperty("placedAt"));
            var dueAt = row.TryGetProperty("dueAt", out var due) ? RationalTime.Read(due) : placedAt + _players[owner - 1].Lifetime;
            RationalTime? explodedAt = row.GetProperty("explodedAt").ValueKind == JsonValueKind.Null ? null : RationalTime.Read(row.GetProperty("explodedAt"));
            int placedMatches = 0, explodedMatches = 0, chains = 0;
            bool initial = row.TryGetProperty("initial", out var init) && init.GetBoolean();
            foreach (var e in history)
            {
                if (e.BubbleId != id) continue;
                if (e.Kind is not (EventKind.Placed or EventKind.InitialBubble or EventKind.Exploded or EventKind.Chained)) continue;
                bubblesMatch &= e.PlayerId == owner && e.Position == position;
                if (e.Kind is EventKind.Placed or EventKind.InitialBubble) { placedMatches++; bubblesMatch &= initial ? e.Kind == EventKind.InitialBubble : e.Kind == EventKind.Placed && e.Tick == placedAt; }
                if (e.Kind == EventKind.Exploded) { explodedMatches++; bubblesMatch &= e.Tick == explodedAt && e.RelatedTime == dueAt; }
                if (e.Kind == EventKind.Chained)
                {
                    chains++;
                    if (row.TryGetProperty("sourceBubbleId", out var source))
                        bubblesMatch &= source.ValueKind != JsonValueKind.Null && e.Value == source.GetInt32() && e.Tick == explodedAt;
                }
            }
            if (row.TryGetProperty("sourceBubbleId", out var expectedSource))
                bubblesMatch &= chains == (expectedSource.ValueKind == JsonValueKind.Null ? 0 : 1);
            bubblesMatch &= placedMatches == 1 && explodedMatches == (explodedAt is null ? 0 : 1) && dueAt == placedAt + _players[owner - 1].Lifetime;
            int retainedMatches = 0;
            foreach (var bubble in Bubbles)
                if (bubble.Id == id)
                {
                    retainedMatches++;
                    bubblesMatch &= bubble.OwnerId == owner && bubble.Position == position && bubble.PlacedAt == placedAt && bubble.DueAt == dueAt;
                }
            bubblesMatch &= retainedMatches == (explodedAt is null ? 1 : 0);
            if (explodedAt is null) expectedRetained++; else expectedExploded++;
        }
        bubblesMatch &= _placed == expectedBubbles.GetArrayLength() && _exploded == expectedExploded && _bubbleCount == expectedRetained;

        int expectedDestroyed = expected.GetProperty("destroyedCrates").GetArrayLength();
        var destroyedMask = ArrayPool<bool>.Shared.Rent(CellCount);
        try
        {
            destroyedMask.AsSpan(0, CellCount).Clear();
            foreach (var json in expected.GetProperty("destroyedCrates").EnumerateArray())
                destroyedMask[ScenarioScript.ReadPosition(json).Index] = true;
            for (int i = 0; i < CellCount; i++)
            {
                var cell = destroyedMask[i] ? Cell.Empty : _initialTerrain[i];
                mapMatches &= _terrain[i] == cell;
                if (_terrain[i] == Cell.Wall) walls++;
            }
        }
        finally { ArrayPool<bool>.Shared.Return(destroyedMask); }
        mapMatches &= CrateCount == _initialCrates - expectedDestroyed && _destroyed == expectedDestroyed && walls == _initialWalls;
        if (expected.TryGetProperty("checkpoints", out var checkpoints))
            foreach (var row in checkpoints.EnumerateArray())
            {
                var at = RationalTime.Read(row.GetProperty("time"));
                foreach (var player in row.GetProperty("players").EnumerateArray())
                {
                    int id = player.GetProperty("playerId").GetInt32(); bool found = false;
                    foreach (var e in history)
                        if (e.Kind == EventKind.State && e.Tick == at && e.PlayerId == id && (!row.TryGetProperty("roundId", out var round) || e.RoundId == round.GetInt32()))
                        {
                            found = true;
                            attributesMatch &= MatchAttributes(player, e.Attributes, e.Pending);
                            playersMatch &= e.Position == ScenarioScript.ReadPosition(player.GetProperty("position")) && e.Success == player.GetProperty("alive").GetBoolean();
                            if (player.TryGetProperty("point", out var point)) playersMatch &= e.Point.X == RationalTime.Read(point.GetProperty("x")) && e.Point.Y == RationalTime.Read(point.GetProperty("y"));
                        }
                    playersMatch &= found;
                }
            }
        if (expected.TryGetProperty("events", out var assertions))
            foreach (var row in assertions.EnumerateArray())
            {
                int matches = 0;
                var kind = Enum.Parse<EventKind>(row.GetProperty("kind").GetString()!, true);
                foreach (var e in history)
                    if (e.Kind == kind && (!row.TryGetProperty("time", out var at) || e.Tick == RationalTime.Read(at)) &&
                        (!row.TryGetProperty("playerId", out var id) || e.PlayerId == id.GetInt32()) &&
                        (!row.TryGetProperty("bubbleId", out var bubbleId) || e.BubbleId == bubbleId.GetInt32()) &&
                        (!row.TryGetProperty("position", out var point) || e.Position == ScenarioScript.ReadPosition(point)) &&
                        (!row.TryGetProperty("reason", out var reason) || e.Reason == Enum.Parse<SkipReason>(reason.GetString()!, true)) &&
                        (!row.TryGetProperty("itemType", out var itemType) || e.ItemType == ItemNames.Parse(itemType.GetString())) &&
                        (!row.TryGetProperty("value", out var value) || e.Value == value.GetInt32()) &&
                        (!row.TryGetProperty("roundId", out var roundId) || e.RoundId == roundId.GetInt32()) &&
                        (!row.TryGetProperty("phase", out var phase) || e.Phase == Enum.Parse<SimulationPhase>(phase.GetString()!, true))) matches++;
                actionsMatch &= matches == row.GetProperty("count").GetInt32();
            }
        bool itemsMatch = ValidateItems(expected);
        if (expected.TryGetProperty("observations", out var observationRows)) _observationsMatch &= _checkedObservationCount == observationRows.GetArrayLength();
        bool passed = playersMatch && mapMatches && bubblesMatch && actionsMatch && outcomeMatches && attributesMatch && itemsMatch && _observationsMatch;
        return new(passed, Tick, CrateCount, walls, _bubbleCount, safe, _placed, _exploded, _destroyed, mapMatches,
            PlayerCount - safe, deaths, playersMatch, bubblesMatch, actionsMatch, outcomeMatches)
        { ItemsMatch = itemsMatch, AttributesMatch = attributesMatch, ObservationsMatch = _observationsMatch };
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<ItemState>.Shared.Return(_items);
        ArrayPool<Cell>.Shared.Return(_terrain);
        ArrayPool<Cell>.Shared.Return(_initialTerrain);
        ArrayPool<MovementSegment>.Shared.Return(_segments);
        ArrayPool<bool>.Shared.Return(_attempted);
        ArrayPool<Player>.Shared.Return(_players);
        ArrayPool<Bubble>.Shared.Return(_bubbles);
        ArrayPool<PlayerAction>.Shared.Return(_tickPlans, true);
        ArrayPool<Move>.Shared.Return(_moves);
        ArrayPool<SimulationEvent>.Shared.Return(_events);
    }
}
