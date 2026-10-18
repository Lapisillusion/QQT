using System.Buffers;
using System.Text.Json;
using QQT.Core;

namespace QQT.ClientAI;

public readonly record struct SelfState(int Id, Position Position, int Speed, int Capacity, int Active, int Range, int Vision, RationalTime Lifetime);
public struct EnemyState
{
    public bool Alive, Located;
    public Position Position;
    public int Speed, Range, Capacity, Active, PositionRound, DetailRound, AttemptRound;
}
public struct TrackedBubble
{
    public Position Position;
    public int QueryId, Owner, Range, ObservedRound;
    public RationalTime Due;
    public bool Known, Estimated, Attempted;
}

/// <summary>一局的唯一记忆所有者；只接收本连接的观察、查询及未确认的自身预测，不读观战现场。</summary>
public sealed class WorldMemory : IDisposable
{
    public readonly Cell[] Terrain = ArrayPool<Cell>.Shared.Rent(GameSimulation.CellCount);
    public readonly int[] Seen = ArrayPool<int>.Shared.Rent(GameSimulation.CellCount);
    public readonly int[] Visits = ArrayPool<int>.Shared.Rent(GameSimulation.CellCount);
    public readonly ItemType[] Items = ArrayPool<ItemType>.Shared.Rent(GameSimulation.CellCount);
    public readonly EnemyState[] Enemies = ArrayPool<EnemyState>.Shared.Rent(5);
    private TrackedBubble[] _bubbles = ArrayPool<TrackedBubble>.Shared.Rent(128);
    private bool _disposed;
    public int BubbleCount { get; private set; }
    public Span<TrackedBubble> Bubbles => _bubbles.AsSpan(0, BubbleCount);
    public SelfState Self { get; private set; }
    public RationalTime Time { get; private set; }
    public int Round { get; private set; }
    public int Budget { get; private set; }
    public Direction PreviousDirection { get; set; }

    public WorldMemory()
    {
        Seen.AsSpan(0, 441).Clear(); Visits.AsSpan(0, 441).Clear(); Items.AsSpan(0, 441).Clear(); Enemies.AsSpan(0, 5).Clear();
        for (int y = 0; y < 21; y++) for (int x = 0; x < 21; x++)
        {
            bool corner = (x < 2 || x > 18) && (y == 0 || y == 20) || (y < 2 || y > 18) && (x == 0 || x == 20);
            Terrain[y * 21 + x] = x % 2 == 1 && y % 2 == 1 ? Cell.Wall : corner ? Cell.Empty : Cell.Crate;
        }
        for (int i = 1; i <= 4; i++) Enemies[i] = new EnemyState { Speed = 1, Range = 1, Capacity = 3 };
    }

    public static Position ReadPosition(JsonElement json) => new(json.GetProperty("x").GetInt32(), json.GetProperty("y").GetInt32());
    public static int Distance(Position a, Position b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    public void Observe(int round, JsonElement payload)
    {
        Round = round; Time = RationalTime.Read(payload.GetProperty("time"));
        var s = payload.GetProperty("self");
        Self = new(s.GetProperty("playerId").GetInt32(), ReadPosition(s.GetProperty("position")), s.GetProperty("speed").GetInt32(),
            s.GetProperty("bubbleCapacity").GetInt32(), s.GetProperty("activeBubbleCount").GetInt32(), s.GetProperty("blastRange").GetInt32(),
            s.GetProperty("visionRadius").GetInt32(), RationalTime.Read(s.GetProperty("bubbleLifetime")));
        Budget = s.GetProperty("actionPoints").GetInt32(); Visits[Self.Position.Index]++;
        var visible = payload.GetProperty("visible");
        foreach (var c in visible.GetProperty("cells").EnumerateArray())
        {
            int i = ReadPosition(c.GetProperty("position")).Index;
            Terrain[i] = c.GetProperty("terrain").GetString() switch { "wall" => Cell.Wall, "crate" => Cell.Crate, "empty" => Cell.Empty, _ => throw new InvalidDataException("未知地形。") };
            Seen[i] = round; Items[i] = ItemType.None;
        }
        foreach (var item in visible.GetProperty("items").EnumerateArray())
            Items[ReadPosition(item.GetProperty("position")).Index] = ItemNames.Parse(item.GetProperty("type").GetString());
        for (int i = 1; i <= 4; i++) Enemies[i].Alive = false;
        foreach (var id in payload.GetProperty("participantIds").EnumerateArray()) Enemies[id.GetInt32()].Alive = true;
        foreach (var other in visible.GetProperty("players").EnumerateArray())
        {
            ref var e = ref Enemies[other.GetProperty("playerId").GetInt32()];
            e.Alive = e.Located = true; e.Position = ReadPosition(other.GetProperty("position")); e.PositionRound = round;
        }
        // 可见区域重新以本轮实体建立查询表，不用上一轮 queryId 或同格顺序猜测实体身份。
        int retained = 0;
        for (int i = 0; i < BubbleCount; i++)
        {
            var b = _bubbles[i];
            if (Seen[b.Position.Index] == round || b.Due <= Time) continue;
            b.QueryId = 0; b.Estimated = true; b.Attempted = false;
            if (b.Owner == Self.Id) b.Range = Self.Range;
            else if (b.Owner > 0) b.Range = Math.Max(b.Range, Enemies[b.Owner].Range);
            _bubbles[retained++] = b;
        }
        BubbleCount = retained;
        foreach (var b in visible.GetProperty("bubbles").EnumerateArray())
            AddBubble(new TrackedBubble { Position = ReadPosition(b.GetProperty("position")), QueryId = b.GetProperty("queryId").GetInt32(),
                ObservedRound = round, Range = MaxKnownRange(), Due = Time + new RationalTime(1, 2) });
    }

    public int MaxKnownRange()
    {
        int range = Self.Range;
        for (int i = 1; i <= 4; i++) if (Enemies[i].Alive) range = Math.Max(range, Enemies[i].Range);
        return range;
    }

    public bool NextQuery(out string type, out int target, out string purpose)
    {
        double best = 0; type = ""; target = 0; purpose = "";
        if (Budget <= 0) return false;
        foreach (var b in Bubbles)
        {
            if (b.QueryId == 0 || b.Attempted || b.Known) continue;
            int distance = Distance(Self.Position, b.Position);
            double value = 1000 - distance * 25;
            if (Self.Position.X == b.Position.X || Self.Position.Y == b.Position.Y) value += 180;
            if (value > best) { best = value; type = "bubble"; target = b.QueryId; purpose = "path-and-chain-timing"; }
        }
        for (int i = 1; i <= 4; i++)
        {
            ref var e = ref Enemies[i];
            int cost = e.PositionRound == Round && InView(e.Position) ? 2 : 3;
            if (!e.Alive || e.AttemptRound == Round || Budget < cost) continue;
            int distance = e.Located ? Distance(Self.Position, e.Position) : 40;
            bool contact = e.Located && distance <= Self.Range + Math.Max(Self.Speed, e.Speed) * 4 + 3;
            if (e.DetailRound > 0 && Round - e.DetailRound < (contact ? 1 : 6)) continue;
            double value = (contact ? 850 : e.Located ? 150 : 300) - distance;
            if (value > best) { best = value; type = "player"; target = i; purpose = contact ? "opponent-escape-and-counterattack" : "locate-opponent"; }
        }
        if (best <= 0) return false;
        if (type == "player") Enemies[target].AttemptRound = Round;
        else for (int i = 0; i < BubbleCount; i++) if (_bubbles[i].QueryId == target) _bubbles[i].Attempted = true;
        return true;
    }

    public void QueryResult(JsonElement payload)
    {
        Budget = payload.GetProperty("actionPoints").GetInt32();
        if (!payload.GetProperty("success").GetBoolean())
        {
            if (payload.GetProperty("error").GetProperty("code").GetString() == "targetEliminated" && payload.GetProperty("targetType").GetString() == "player")
                Enemies[payload.GetProperty("targetId").GetInt32()].Alive = false;
            return;
        }
        int id = payload.GetProperty("targetId").GetInt32(); var data = payload.GetProperty("data");
        if (payload.GetProperty("targetType").GetString() == "player")
        {
            ref var e = ref Enemies[id]; e.Located = true; e.Position = ReadPosition(data.GetProperty("position"));
            e.Speed = data.GetProperty("speed").GetInt32(); e.Range = data.GetProperty("blastRange").GetInt32();
            e.Capacity = data.GetProperty("bubbleCapacity").GetInt32(); e.Active = data.GetProperty("activeBubbleCount").GetInt32();
            e.PositionRound = e.DetailRound = Round;
        }
        else for (int i = 0; i < BubbleCount; i++) if (_bubbles[i].QueryId == id)
        {
            ref var b = ref _bubbles[i]; b.Owner = data.GetProperty("ownerId").GetInt32(); b.Range = data.GetProperty("blastRange").GetInt32();
            b.Due = RationalTime.Read(data.GetProperty("dueAt")); b.Known = true; b.Estimated = false; break;
        }
    }

    public bool InView(Position p) => Math.Abs(p.X - Self.Position.X) <= Self.Vision && Math.Abs(p.Y - Self.Position.Y) <= Self.Vision;
    public void AddBubble(TrackedBubble bubble)
    {
        if (BubbleCount == _bubbles.Length)
        {
            var next = ArrayPool<TrackedBubble>.Shared.Rent(checked(BubbleCount * 2));
            _bubbles.AsSpan(0, BubbleCount).CopyTo(next); ArrayPool<TrackedBubble>.Shared.Return(_bubbles); _bubbles = next;
        }
        _bubbles[BubbleCount++] = bubble;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ArrayPool<Cell>.Shared.Return(Terrain); ArrayPool<int>.Shared.Return(Seen); ArrayPool<int>.Shared.Return(Visits);
        ArrayPool<ItemType>.Shared.Return(Items); ArrayPool<EnemyState>.Shared.Return(Enemies); ArrayPool<TrackedBubble>.Shared.Return(_bubbles);
    }
}
