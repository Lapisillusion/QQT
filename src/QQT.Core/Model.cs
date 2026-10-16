namespace QQT.Core;

public enum Cell : byte { Empty, Wall, Crate }
public enum Direction : byte { Wait, Up, Down, Left, Right }
public enum EventKind : byte { Move, Blocked, Placed, Exploded, Covered, CrateDestroyed, Safety, PlacementSkipped, Death, ActionIgnored, Chained, MatchEnded, Entered, ReturnStarted, Returned, State, InitialBubble }
public enum SkipReason : byte { None, Dead, NotReached, Capacity }

public readonly record struct Position(int X, int Y)
{
    public int Index => Y * GameSimulation.Size + X;
    public bool InBounds => X is >= 0 and < GameSimulation.Size && Y is >= 0 and < GameSimulation.Size;
    public Position Next(Direction direction) => direction switch
    {
        Direction.Up => new(X, Y - 1), Direction.Down => new(X, Y + 1),
        Direction.Left => new(X - 1, Y), Direction.Right => new(X + 1, Y), _ => this
    };
}

public struct Player
{
    public int Id;
    public Position Position;
    public bool Hit;
    public RationalTime? DeathTick;
    public int Speed, Capacity, Range;
    public RationalTime Lifetime;
    public ExactPosition Point;
    public int OwnedBubblesAtDeath;
    public readonly bool Alive => !Hit;
}

public readonly record struct Bubble(int Id, int OwnerId, Position Position, RationalTime PlacedAt, RationalTime DueAt);
public readonly record struct ExactPosition(RationalTime X, RationalTime Y)
{
    public static ExactPosition Center(Position p) => new(p.X, p.Y);
}
public readonly record struct Move(int PlayerId, ExactPosition From, ExactPosition To, RationalTime Start, RationalTime End);
/// <summary>Placements 借用脚本持有的池数组；脚本须存活至本轮执行完毕。</summary>
public readonly record struct PlayerAction(int PlayerId, Direction Direction, Position? Placement)
{
    public ReadOnlyMemory<Position> Placements { get; init; }
    public int PlacementCount => Placements.IsEmpty ? (Placement is null ? 0 : 1) : Placements.Length;
    public Position PlacementAt(int index) => Placements.IsEmpty ? Placement!.Value : Placements.Span[index];
}
public readonly record struct SimulationEvent(EventKind Kind, RationalTime Tick, int PlayerId, int BubbleId, Position Position, Position From, bool Success, int Value = 0, SkipReason Reason = SkipReason.None)
{
    public ExactPosition Point { get; init; }
    public RationalTime RelatedTime { get; init; }
    public int RoundId { get; init; }
}
public readonly record struct ValidationResult(bool Passed, int Tick, int Crates, int Walls, int Bubbles, int SafePlayers, int Placed, int Exploded, int Destroyed, bool MapMatches, int DeadPlayers, int Deaths, bool PlayersMatch, bool BubblesMatch, bool ActionsMatch, bool OutcomeMatches);
