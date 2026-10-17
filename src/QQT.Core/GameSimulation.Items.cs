using System.Buffers;
using System.Text.Json;

namespace QQT.Core;

public sealed partial class GameSimulation
{
    // 世界唯一持有，跨轮复用；终态保留便于 UI 和脚本核对，Reset 清空，Dispose 归还。
    private readonly ItemState[] _items = ArrayPool<ItemState>.Shared.Rent(CellCount);
    private bool _itemsConfigured, _roundPrepared, _observationsMatch = true;
    private SimulationPhase _phase;
    private int _checkedObservationCount;
    public ReadOnlySpan<ItemState> Items => _items.AsSpan(0, CellCount);
    public string ItemMode { get; private set; } = "none";
    public int? ItemSeed { get; private set; }
    public ItemGains RequestedItems { get; private set; }
    public ItemGains GeneratedItems { get; private set; }

    private void ResetItems()
    {
        _items.AsSpan(0, CellCount).Clear();
        _itemsConfigured = _roundPrepared = false; _observationsMatch = true;
        _phase = SimulationPhase.Execution; _checkedObservationCount = 0;
        ItemMode = "none"; ItemSeed = null; RequestedItems = GeneratedItems = default;
    }

    /// <summary>最终地图确定后、首次准备轮次前调用一次。配置借用 JSON，仅在此调用内读取。</summary>
    public void ConfigureItems(JsonElement? configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Tick != 0 || Time != 0 || _roundPrepared || _itemsConfigured) throw new InvalidOperationException("道具只能在开局初始化一次。");
        _itemsConfigured = true;
        if (configuration is not { } config) return;
        _phase = SimulationPhase.Initialization;
        try
        {
            ItemMode = config.GetProperty("mode").GetString() ?? throw new InvalidDataException("缺少道具模式。");
            if (ItemMode == "fixed")
            {
                if (config.TryGetProperty("counts", out _) || config.TryGetProperty("seed", out _)) throw new InvalidDataException("固定模式不能包含随机配置。");
                foreach (var row in config.GetProperty("placements").EnumerateArray())
                {
                    var position = ScenarioScript.ReadPosition(row.GetProperty("position"));
                    var type = ItemNames.Parse(row.GetProperty("type").GetString());
                    if (_terrain[position.Index] != Cell.Crate || _items[position.Index].Type != ItemType.None) throw new InvalidDataException("固定道具必须位于不同的初始箱子中。");
                    RequestedItems = RequestedItems.Add(type, 1); GenerateItem(position, type);
                }
            }
            else if (ItemMode == "random")
            {
                if (config.TryGetProperty("placements", out _)) throw new InvalidDataException("随机模式不能包含固定位置。");
                var counts = config.GetProperty("counts");
                foreach (var property in counts.EnumerateObject()) ItemNames.Parse(property.Name);
                for (var type = ItemType.Speed; type <= ItemType.Vision; type++)
                {
                    int count = counts.GetProperty(ItemNames.Name(type)).GetInt32();
                    if (count < 0) throw new InvalidDataException("道具数量不能为负。");
                    RequestedItems = RequestedItems.Add(type, count);
                }
                ItemSeed = config.TryGetProperty("seed", out var seed) ? seed.GetInt32() : Random.Shared.Next();
                var random = new Random(ItemSeed.Value);
                var positions = ArrayPool<Position>.Shared.Rent(CellCount);
                try
                {
                    int count = 0;
                    for (int i = 0; i < CellCount; i++) if (_terrain[i] == Cell.Crate) positions[count++] = new(i % Size, i / Size);
                    for (int i = count - 1; i > 0; i--)
                    {
                        int j = random.Next(i + 1);
                        (positions[i], positions[j]) = (positions[j], positions[i]);
                    }
                    var remaining = RequestedItems;
                    for (int i = 0; i < count && remaining.Total > 0; i++)
                    {
                        // 按剩余需求加权，无放回抽样。计数和用 long，循环次数不超过箱子数。
                        long draw = random.NextInt64(remaining.Total);
                        for (var type = ItemType.Speed; type <= ItemType.Vision; type++)
                        {
                            int weight = remaining.Get(type);
                            if (draw >= weight) { draw -= weight; continue; }
                            GenerateItem(positions[i], type); remaining = remaining.Add(type, -1); break;
                        }
                    }
                }
                finally { ArrayPool<Position>.Shared.Return(positions); }
            }
            else throw new InvalidDataException("道具模式必须是 fixed 或 random。");
        }
        finally { _phase = SimulationPhase.Execution; }
    }

    private void GenerateItem(Position position, ItemType type)
    {
        _items[position.Index] = new(type, ItemStatus.Hidden);
        GeneratedItems = GeneratedItems.Add(type, 1);
        Add(new(EventKind.ItemGenerated, Time, 0, 0, position, position, true) { ItemType = type });
    }
    private void DestroyExposedItems(bool[] covered)
    {
        for (int i = 0; i < CellCount; i++) if (covered[i] && _items[i].Status == ItemStatus.Exposed)
        {
            var item = _items[i]; var position = new Position(i % Size, i / Size);
            _items[i] = item with { Status = ItemStatus.Destroyed };
            Add(new(EventKind.ItemDestroyed, Time, 0, 0, position, position, true) { ItemType = item.Type });
        }
    }
    private void ExposeItem(Position position)
    {
        var item = _items[position.Index];
        if (item.Status != ItemStatus.Hidden) return;
        _items[position.Index] = item with { Status = ItemStatus.Exposed };
        Add(new(EventKind.ItemExposed, Time, 0, 0, position, position, true) { ItemType = item.Type });
    }
    private void PickItems()
    {
        // 按道具先确定所有获得者再移除，不依赖玩家顺序；判断真实中心，不沿用放置的回退筛选。
        for (int cell = 0; cell < CellCount; cell++) if (_items[cell].Status == ItemStatus.Exposed)
        {
            var position = new Position(cell % Size, cell / Size); var item = _items[cell];
            bool picked = false;
            for (int i = 0; i < 4; i++)
            {
                ref var player = ref _players[i];
                if (!player.Alive || player.Position != position || player.Point != ExactPosition.Center(position)) continue;
                player.Pending = player.Pending.Add(item.Type, 1); picked = true;
                Add(new(EventKind.ItemPicked, Time, player.Id, 0, position, position, true, 1) { ItemType = item.Type });
            }
            if (picked) _items[cell] = item with { Status = ItemStatus.Picked };
        }
    }

    /// <summary>幂等地准备下一轮。激活增益后可建立独立观察；尚未执行旧轮终点或已终局时不得越过此边界。</summary>
    public void PrepareRound()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ended) throw new InvalidOperationException("已结束的对局不能准备新轮。");
        if (_roundPrepared) return;
        _eventCount = _moveCount = 0; _roundPrepared = true; _phase = SimulationPhase.RoundStart;
        for (int i = 0; i < 4; i++)
        {
            ref var player = ref _players[i]; var gains = player.Pending;
            player.Pending = default;
            for (var type = ItemType.Speed; type <= ItemType.Vision; type++)
            {
                int delta = gains.Get(type); if (delta == 0) continue;
                int before = type switch { ItemType.Speed => player.Speed, ItemType.Capacity => player.Capacity, ItemType.Range => player.Range, _ => player.Vision };
                int after = checked(before + delta);
                switch (type)
                {
                    case ItemType.Speed: player.Speed = after; break;
                    case ItemType.Capacity: player.Capacity = after; break;
                    case ItemType.Range: player.Range = after; break;
                    case ItemType.Vision: player.Vision = after; break;
                }
                Add(new(EventKind.AttributeActivated, Time, player.Id, 0, player.Position, player.Position, true, after) { ItemType = type, PreviousValue = before });
            }
        }
    }

    private static bool MatchAttributes(JsonElement row, PlayerAttributes values, ItemGains pending)
    {
        bool same = (!row.TryGetProperty("speed", out var speed) || speed.GetInt32() == values.Speed) &&
            (!row.TryGetProperty("capacity", out var cap) || cap.GetInt32() == values.Capacity) &&
            (!row.TryGetProperty("range", out var range) || range.GetInt32() == values.Range) &&
            (!row.TryGetProperty("vision", out var vision) || vision.GetInt32() == values.Vision);
        if (row.TryGetProperty("pending", out var gains))
            for (var type = ItemType.Speed; type <= ItemType.Vision; type++)
                same &= gains.GetProperty(ItemNames.Name(type)).GetInt32() == pending.Get(type);
        return same;
    }
    private bool ValidateItems(JsonElement expected)
    {
        bool same = true;
        if (expected.TryGetProperty("items", out var rows))
        {
            var seen = ArrayPool<bool>.Shared.Rent(CellCount);
            try
            {
                seen.AsSpan(0, CellCount).Clear();
                foreach (var row in rows.EnumerateArray())
                {
                    int index = ScenarioScript.ReadPosition(row.GetProperty("position")).Index;
                    same &= !seen[index]; seen[index] = true;
                    same &= _items[index].Type == ItemNames.Parse(row.GetProperty("type").GetString()) &&
                        _items[index].Status == Enum.Parse<ItemStatus>(row.GetProperty("status").GetString()!, true);
                }
                for (int i = 0; i < CellCount; i++) same &= seen[i] == (_items[i].Type != ItemType.None);
            }
            finally { ArrayPool<bool>.Shared.Return(seen); }
        }
        // 校验真实格子状态与生成计数一致，不能只比较两个来源相同的摘要字段。
        for (var type = ItemType.Speed; type <= ItemType.Vision; type++)
        {
            int actual = 0;
            for (int i = 0; i < CellCount; i++) if (_items[i].Type == type)
            {
                actual++; same &= _initialTerrain[i] == Cell.Crate && _items[i].Status != ItemStatus.None;
            }
            same &= actual == GeneratedItems.Get(type);
            if (expected.TryGetProperty("itemCounts", out var counts)) same &= counts.GetProperty(ItemNames.Name(type)).GetInt32() == actual;
        }
        return same;
    }
}
