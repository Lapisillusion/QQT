using QQT.Core;

namespace QQT.Server;

/// <summary>Core 只产生值事件；宿主将同一事件映射到分类诊断日志。</summary>
public sealed partial class SimulationLogger
{
    private readonly ILogger _tick, _movement, _bubble, _explosion, _map, _script, _death, _match;
    public SimulationLogger(ILoggerFactory factory)
    {
        _tick = factory.CreateLogger("QQT.Simulation.Tick");
        _movement = factory.CreateLogger("QQT.Simulation.Tick.Movement");
        _bubble = factory.CreateLogger("QQT.Simulation.Tick.Bubble");
        _explosion = factory.CreateLogger("QQT.Simulation.Tick.Explosion");
        _map = factory.CreateLogger("QQT.Simulation.Tick.Map");
        _death = factory.CreateLogger("QQT.Simulation.Tick.Death");
        _script = factory.CreateLogger("QQT.Script");
        _match = factory.CreateLogger("QQT.Simulation.Match");
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
            case EventKind.PlacementSkipped:
                if (e.Reason == SkipReason.Dead) DeathPlacementCancelled(_death, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y);
                else Skipped(_bubble, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y);
                break;
            case EventKind.Death: Died(_death, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y, e.Value); break;
            case EventKind.ActionIgnored: Ignored(_death, runId, e.Tick, e.PlayerId, e.Value); break;
            case EventKind.Exploded: Exploded(_explosion, runId, e.Tick, e.PlayerId, e.BubbleId, e.Position.X, e.Position.Y, e.Value); break;
            case EventKind.Chained: Chained(_explosion, runId, e.Tick, e.PlayerId, e.BubbleId, e.Value, e.Position.X, e.Position.Y); break;
            case EventKind.MatchEnded: Ended(_match, runId, e.Tick, e.PlayerId, e.Value); break;
            case EventKind.Covered: Covered(_explosion, runId, e.Tick, e.BubbleId, e.Position.X, e.Position.Y); break;
            case EventKind.CrateDestroyed: Destroyed(_map, runId, e.Tick, e.Position.X, e.Position.Y); break;
            case EventKind.Safety: Safety(_explosion, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y, e.Success); break;
        }
    }
    public void Validated(string runId, GameSimulation world, ValidationResult result)
    {
        foreach (var player in world.Players) FinalPlayer(_script, runId, player.Id, player.Position.X, player.Position.Y, player.Alive, player.DeathTick);
        Result(_script, runId, result.Passed, result.Tick, result.Crates, result.Walls, result.Bubbles, result.SafePlayers,
            result.Placed, result.Exploded, result.Destroyed, result.MapMatches);
        DeathValidation(_script, runId, result.DeadPlayers, result.Deaths, result.PlayersMatch, result.BubblesMatch, result.ActionsMatch);
        foreach (var bubble in world.Bubbles) FinalBubble(_script, runId, bubble.Id, bubble.OwnerId, bubble.Position.X, bubble.Position.Y, bubble.PlacedAt, bubble.DueAt);
        OutcomeValidation(_script, runId, world.Ended, world.WinnerId, world.EndTick, result.OutcomeMatches);
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
    [LoggerMessage(400, LogLevel.Information, "BUBBLE_EXPLODED RunId={RunId} Tick={Tick} PlayerId={PlayerId} BubbleId={BubbleId} Position=({X},{Y}) DueAt={DueAt}")]
    private static partial void Exploded(ILogger logger, string runId, int tick, int playerId, int bubbleId, int x, int y, int dueAt);
    [LoggerMessage(403, LogLevel.Information, "BUBBLE_CHAINED RunId={RunId} Tick={Tick} PlayerId={PlayerId} BubbleId={BubbleId} SourceBubbleId={SourceBubbleId} Position=({X},{Y})")]
    private static partial void Chained(ILogger logger, string runId, int tick, int playerId, int bubbleId, int sourceBubbleId, int x, int y);
    [LoggerMessage(401, LogLevel.Trace, "BLAST_CELL RunId={RunId} Tick={Tick} BubbleId={BubbleId} Position=({X},{Y})")]
    private static partial void Covered(ILogger logger, string runId, int tick, int bubbleId, int x, int y);
    [LoggerMessage(402, LogLevel.Information, "SAFETY RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y}) Safe={Safe}")]
    private static partial void Safety(ILogger logger, string runId, int tick, int playerId, int x, int y, bool safe);
    [LoggerMessage(500, LogLevel.Information, "CRATE_DESTROYED RunId={RunId} Tick={Tick} Position=({X},{Y})")]
    private static partial void Destroyed(ILogger logger, string runId, int tick, int x, int y);
    [LoggerMessage(600, LogLevel.Information, "FINAL_PLAYER RunId={RunId} PlayerId={PlayerId} Position=({X},{Y}) Alive={Alive} DeathTick={DeathTick}")]
    private static partial void FinalPlayer(ILogger logger, string runId, int playerId, int x, int y, bool alive, int? deathTick);
    [LoggerMessage(601, LogLevel.Information, "VALIDATION RunId={RunId} Passed={Passed} Tick={Tick} Crates={Crates} Walls={Walls} Bubbles={Bubbles} SafePlayers={SafePlayers} Placed={Placed} Exploded={Exploded} Destroyed={Destroyed} MapMatches={MapMatches}")]
    private static partial void Result(ILogger logger, string runId, bool passed, int tick, int crates, int walls, int bubbles, int safePlayers, int placed, int exploded, int destroyed, bool mapMatches);
    [LoggerMessage(602, LogLevel.Error, "VALIDATION_FAILED RunId={RunId}; compare final positions, timing and counters with M0 expectations")]
    private static partial void Failed(ILogger logger, string runId);
    [LoggerMessage(603, LogLevel.Error, "SCENARIO_ERROR RunId={RunId}")]
    private static partial void ScriptError(ILogger logger, Exception exception, string runId);
    [LoggerMessage(604, LogLevel.Information, "FINAL_BUBBLE RunId={RunId} BubbleId={BubbleId} OwnerId={OwnerId} Position=({X},{Y}) PlacedAt={PlacedAt} DueAt={DueAt}")]
    private static partial void FinalBubble(ILogger logger, string runId, int bubbleId, int ownerId, int x, int y, int placedAt, int dueAt);
    [LoggerMessage(605, LogLevel.Information, "OUTCOME_VALIDATION RunId={RunId} Ended={Ended} WinnerId={WinnerId} EndTick={EndTick} OutcomeMatches={OutcomeMatches}")]
    private static partial void OutcomeValidation(ILogger logger, string runId, bool ended, int? winnerId, int? endTick, bool outcomeMatches);
    [LoggerMessage(800, LogLevel.Information, "MATCH_ENDED RunId={RunId} Tick={Tick} WinnerId={WinnerId} Survivors={Survivors}; WinnerId=0 means draw")]
    private static partial void Ended(ILogger logger, string runId, int tick, int winnerId, int survivors);
    [LoggerMessage(700, LogLevel.Information, "PLAYER_DIED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y}) OwnedBubblesRemaining={OwnedBubbles}")]
    private static partial void Died(ILogger logger, string runId, int tick, int playerId, int x, int y, int ownedBubbles);
    [LoggerMessage(701, LogLevel.Information, "DEAD_PLACEMENT_CANCELLED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y})")]
    private static partial void DeathPlacementCancelled(ILogger logger, string runId, int tick, int playerId, int x, int y);
    [LoggerMessage(702, LogLevel.Information, "DEAD_ACTION_IGNORED RunId={RunId} Tick={Tick} PlayerId={PlayerId} PlannedRound={PlannedRound}")]
    private static partial void Ignored(ILogger logger, string runId, int tick, int playerId, int plannedRound);
    [LoggerMessage(703, LogLevel.Information, "DEATH_VALIDATION RunId={RunId} DeadPlayers={DeadPlayers} Deaths={Deaths} PlayersMatch={PlayersMatch} BubblesMatch={BubblesMatch} ActionsMatch={ActionsMatch}")]
    private static partial void DeathValidation(ILogger logger, string runId, int deadPlayers, int deaths, bool playersMatch, bool bubblesMatch, bool actionsMatch);
}
