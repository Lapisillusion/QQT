namespace QQT.Core;

public enum Cell : byte { Empty, Wall, Crate }
public enum Direction : byte { Wait, Up, Down, Left, Right }
public enum EventKind : byte { Move, Blocked, Placed, Exploded, Covered, CrateDestroyed, Safety, PlacementSkipped, Death, ActionIgnored }
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
    public int? DeathTick;
    public int OwnedBubblesAtDeath;
    public readonly bool Alive => !Hit;
}

public readonly record struct Bubble(int Id, int OwnerId, Position Position, int PlacedAt, int DueAt);
public readonly record struct Move(int PlayerId, Position From, Position To);
public readonly record struct PlayerAction(int PlayerId, Direction Direction, Position? Placement);
public readonly record struct SimulationEvent(EventKind Kind, int Tick, int PlayerId, int BubbleId, Position Position, Position From, bool Success, int Value = 0, SkipReason Reason = SkipReason.None);
public readonly record struct ValidationResult(bool Passed, int Tick, int Crates, int Walls, int Bubbles, int SafePlayers, int Placed, int Exploded, int Destroyed, bool MapMatches, int DeadPlayers, int Deaths, bool PlayersMatch, bool BubblesMatch, bool ActionsMatch);
