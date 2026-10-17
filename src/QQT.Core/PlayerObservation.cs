using System.Buffers;

namespace QQT.Core;

public readonly record struct ObservedCell(Position Position, Cell Terrain);
public readonly record struct ObservedPlayer(int Id, Position Position);
public readonly record struct ObservedBubble(int QueryId, Position Position);
public readonly record struct ObservedItem(Position Position, ItemType Type, int Effect = 1);

/// <summary>调用者唯一持有的冻结观察。数组不借用世界内存，跨 Advance/Reset 稳定；最后使用者 Dispose 后不可访问。</summary>
public sealed class PlayerObservation : IDisposable
{
    private ObservedCell[]? _cells;
    private ObservedPlayer[]? _players;
    private ObservedBubble[]? _bubbles;
    private ObservedItem[]? _items;
    private int _cellCount, _playerCount, _bubbleCount, _itemCount;
    public Player Self { get; }
    public int RoundId { get; }
    public RationalTime Time { get; }
    public ReadOnlySpan<ObservedCell> Cells { get { Guard(); return _cells.AsSpan(0, _cellCount); } }
    public ReadOnlySpan<ObservedPlayer> Players { get { Guard(); return _players.AsSpan(0, _playerCount); } }
    public ReadOnlySpan<ObservedBubble> Bubbles { get { Guard(); return _bubbles.AsSpan(0, _bubbleCount); } }
    public ReadOnlySpan<ObservedItem> Items { get { Guard(); return _items.AsSpan(0, _itemCount); } }
    private void Guard() => ObjectDisposedException.ThrowIf(_cells is null, this);
    internal PlayerObservation(GameSimulation world, int playerId)
    {
        Self = world.Players[playerId - 1]; RoundId = world.Tick + 1; Time = world.Time;
        try
        {
            _cells = ArrayPool<ObservedCell>.Shared.Rent(GameSimulation.CellCount);
            _players = ArrayPool<ObservedPlayer>.Shared.Rent(4);
            _bubbles = ArrayPool<ObservedBubble>.Shared.Rent(Math.Max(1, world.BubbleCount));
            _items = ArrayPool<ObservedItem>.Shared.Rent(GameSimulation.CellCount);
            int left = (int)Math.Max(0, (long)Self.Position.X - Self.Vision), right = (int)Math.Min(GameSimulation.Size - 1, (long)Self.Position.X + Self.Vision);
            int top = (int)Math.Max(0, (long)Self.Position.Y - Self.Vision), bottom = (int)Math.Min(GameSimulation.Size - 1, (long)Self.Position.Y + Self.Vision);
            for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
            {
                var position = new Position(x, y);
                _cells[_cellCount++] = new(position, world.Terrain[position.Index]);
                var item = world.Items[position.Index];
                if (item.Status == ItemStatus.Exposed) _items[_itemCount++] = new(position, item.Type);
            }
            foreach (var player in world.Players)
                if (player.Id != playerId && player.Alive && InView(player.Position)) _players[_playerCount++] = new(player.Id, player.Position);
            foreach (var bubble in world.Bubbles)
                if (InView(bubble.Position)) { _bubbles[_bubbleCount] = new(_bubbleCount + 1, bubble.Position); _bubbleCount++; }
        }
        catch { Dispose(); throw; }
    }
    private bool InView(Position p) => Math.Abs((long)p.X - Self.Position.X) <= Self.Vision && Math.Abs((long)p.Y - Self.Position.Y) <= Self.Vision;
    public void Dispose()
    {
        var cells = Interlocked.Exchange(ref _cells, null); if (cells is not null) ArrayPool<ObservedCell>.Shared.Return(cells);
        var players = Interlocked.Exchange(ref _players, null); if (players is not null) ArrayPool<ObservedPlayer>.Shared.Return(players);
        var bubbles = Interlocked.Exchange(ref _bubbles, null); if (bubbles is not null) ArrayPool<ObservedBubble>.Shared.Return(bubbles);
        var items = Interlocked.Exchange(ref _items, null); if (items is not null) ArrayPool<ObservedItem>.Shared.Return(items);
    }
}
