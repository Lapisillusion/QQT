using QQT.Core;

namespace QQT.Server;

/// <summary>Core 只产生值事件；宿主将同一事件映射到分类诊断日志。</summary>
public sealed partial class SimulationLogger
{
    private readonly ILogger _tick, _movement, _bubble, _explosion, _map, _script;
    public SimulationLogger(ILoggerFactory factory)
    {
        _tick = factory.CreateLogger("QQT.Simulation.Tick");
        _movement = factory.CreateLogger("QQT.Simulation.Tick.Movement");
        _bubble = factory.CreateLogger("QQT.Simulation.Tick.Bubble");
        _explosion = factory.CreateLogger("QQT.Simulation.Tick.Explosion");
        _map = factory.CreateLogger("QQT.Simulation.Tick.Map");
        _script = factory.CreateLogger("QQT.Script");
    }
    public void Started(string runId, string name) => ScriptStarted(_script, runId, name);
    public void TickStarted(string runId, int tick) => BeginTick(_tick, runId, tick);
    public void TickEnded(string runId, GameSimulation world) => EndTick(_tick, runId, world.Tick, world.BubbleCount, world.CrateCount);
    public void Error(string runId, Exception exception) => ScriptError(_script, exception, runId);
    public void Write(string runId, SimulationEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.Move: Movement(_movement, runId, e.Tick, e.PlayerId, e.From.X, e.From.Y, e.Position.X, e.Position.Y); break;
            case EventKind.Blocked: Blocked(_movement, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y); break;
            case EventKind.Placed: Placed(_bubble, runId, e.Tick, e.PlayerId, e.BubbleId, e.Position.X, e.Position.Y, e.Tick + 3); break;
            case EventKind.PlacementSkipped: Skipped(_bubble, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y); break;
            case EventKind.Exploded: Exploded(_explosion, runId, e.Tick, e.BubbleId, e.Position.X, e.Position.Y); break;
            case EventKind.Covered: Covered(_explosion, runId, e.Tick, e.BubbleId, e.Position.X, e.Position.Y); break;
            case EventKind.CrateDestroyed: Destroyed(_map, runId, e.Tick, e.Position.X, e.Position.Y); break;
            case EventKind.Safety: Safety(_explosion, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y, e.Success); break;
        }
    }
    public void Validated(string runId, GameSimulation world, ValidationResult result)
    {
        foreach (var player in world.Players) FinalPlayer(_script, runId, player.Id, player.Position.X, player.Position.Y, player.Hit);
        Result(_script, runId, result.Passed, result.Tick, result.Crates, result.Walls, result.Bubbles, result.SafePlayers,
            result.Placed, result.Exploded, result.Destroyed, result.MapMatches);
        if (!result.Passed) Failed(_script, runId);
    }

    [LoggerMessage(100, LogLevel.Information, "SCENARIO_START RunId={RunId} Name={Name}")]
    private static partial void ScriptStarted(ILogger logger, string runId, string name);
    [LoggerMessage(101, LogLevel.Information, "TICK_START RunId={RunId} Tick={Tick}")]
    private static partial void BeginTick(ILogger logger, string runId, int tick);
    [LoggerMessage(102, LogLevel.Information, "TICK_END RunId={RunId} Tick={Tick} Bubbles={Bubbles} Crates={Crates}")]
    private static partial void EndTick(ILogger logger, string runId, int tick, int bubbles, int crates);
    [LoggerMessage(200, LogLevel.Debug, "MOVE RunId={RunId} Tick={Tick} PlayerId={PlayerId} From=({FromX},{FromY}) To=({X},{Y})")]
    private static partial void Movement(ILogger logger, string runId, int tick, int playerId, int fromX, int fromY, int x, int y);
    [LoggerMessage(201, LogLevel.Debug, "BLOCKED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Target=({X},{Y})")]
    private static partial void Blocked(ILogger logger, string runId, int tick, int playerId, int x, int y);
    [LoggerMessage(300, LogLevel.Information, "BUBBLE_PLACED RunId={RunId} Tick={Tick} PlayerId={PlayerId} BubbleId={BubbleId} Position=({X},{Y}) DueAt={DueAt}")]
    private static partial void Placed(ILogger logger, string runId, int tick, int playerId, int bubbleId, int x, int y, int dueAt);
    [LoggerMessage(301, LogLevel.Debug, "PLACEMENT_SKIPPED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y})")]
    private static partial void Skipped(ILogger logger, string runId, int tick, int playerId, int x, int y);
    [LoggerMessage(400, LogLevel.Information, "BUBBLE_EXPLODED RunId={RunId} Tick={Tick} BubbleId={BubbleId} Position=({X},{Y})")]
    private static partial void Exploded(ILogger logger, string runId, int tick, int bubbleId, int x, int y);
    [LoggerMessage(401, LogLevel.Trace, "BLAST_CELL RunId={RunId} Tick={Tick} BubbleId={BubbleId} Position=({X},{Y})")]
    private static partial void Covered(ILogger logger, string runId, int tick, int bubbleId, int x, int y);
    [LoggerMessage(402, LogLevel.Information, "SAFETY RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y}) Safe={Safe}")]
    private static partial void Safety(ILogger logger, string runId, int tick, int playerId, int x, int y, bool safe);
    [LoggerMessage(500, LogLevel.Information, "CRATE_DESTROYED RunId={RunId} Tick={Tick} Position=({X},{Y})")]
    private static partial void Destroyed(ILogger logger, string runId, int tick, int x, int y);
    [LoggerMessage(600, LogLevel.Information, "FINAL_PLAYER RunId={RunId} PlayerId={PlayerId} Position=({X},{Y}) Hit={Hit}")]
    private static partial void FinalPlayer(ILogger logger, string runId, int playerId, int x, int y, bool hit);
    [LoggerMessage(601, LogLevel.Information, "VALIDATION RunId={RunId} Passed={Passed} Tick={Tick} Crates={Crates} Walls={Walls} Bubbles={Bubbles} SafePlayers={SafePlayers} Placed={Placed} Exploded={Exploded} Destroyed={Destroyed} MapMatches={MapMatches}")]
    private static partial void Result(ILogger logger, string runId, bool passed, int tick, int crates, int walls, int bubbles, int safePlayers, int placed, int exploded, int destroyed, bool mapMatches);
    [LoggerMessage(602, LogLevel.Error, "VALIDATION_FAILED RunId={RunId}; compare final positions, timing and counters with M0 expectations")]
    private static partial void Failed(ILogger logger, string runId);
    [LoggerMessage(603, LogLevel.Error, "SCENARIO_ERROR RunId={RunId}")]
    private static partial void ScriptError(ILogger logger, Exception exception, string runId);
}
