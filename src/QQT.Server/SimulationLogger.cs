using QQT.Core;

namespace QQT.Server;

/// <summary>Core 只产生值事件；宿主将同一事件映射到分类诊断日志。</summary>
public sealed partial class SimulationLogger
{
    private readonly ILogger _tick, _movement, _bubble, _explosion, _map, _script, _death, _match, _item;
    public SimulationLogger(ILoggerFactory factory)
    {
        _item = factory.CreateLogger("QQT.Simulation.Tick.Item");
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
            case EventKind.ItemGenerated:
                Generated(_item, runId, e.Position.X, e.Position.Y, ItemNames.Name(e.ItemType)); break;
            case EventKind.ItemExposed: case EventKind.ItemDestroyed: case EventKind.ItemPicked: case EventKind.AttributeActivated:
                int after = e.Kind == EventKind.ItemPicked ? e.Pending.Get(e.ItemType) : e.Value;
                int before = e.Kind == EventKind.ItemPicked ? after - 1 : e.PreviousValue;
                ItemChanged(_item, runId, e.RoundId, e.Tick, e.Phase, e.Kind, e.PlayerId, e.Position.X, e.Position.Y, ItemNames.Name(e.ItemType), before, after); break;
            case EventKind.Entered: case EventKind.ReturnStarted: case EventKind.Returned:
                MovementPhase(_movement, runId, e.Tick, e.PlayerId, e.Kind == EventKind.Entered ? "Entered" : e.Kind == EventKind.ReturnStarted ? "ReturnStarted" : "Returned", e.Position.X, e.Position.Y, e.Point.X, e.Point.Y); break;
            case EventKind.State: break;
            case EventKind.InitialBubble: InitialBubble(_bubble, runId, e.Tick, e.PlayerId, e.BubbleId, e.Position.X, e.Position.Y, e.RelatedTime); break;
            case EventKind.Move: Movement(_movement, runId, e.Tick, e.PlayerId, e.From.X, e.From.Y, e.Position.X, e.Position.Y); break;
            case EventKind.Blocked: Blocked(_movement, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y); break;
            case EventKind.Placed: Placed(_bubble, runId, e.Tick, e.PlayerId, e.BubbleId, e.Position.X, e.Position.Y, e.RelatedTime); break;
            case EventKind.PlacementSkipped:
                if (e.Reason == SkipReason.Dead) DeathPlacementCancelled(_death, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y);
                else Skipped(_bubble, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y, e.Reason == SkipReason.Capacity ? "Capacity" : "NotReached");
                break;
            case EventKind.Death: Died(_death, runId, e.Tick, e.PlayerId, e.Position.X, e.Position.Y, e.Value); break;
            case EventKind.ActionIgnored: Ignored(_death, runId, e.Tick, e.PlayerId, e.Value); break;
            case EventKind.Exploded: Exploded(_explosion, runId, e.Tick, e.PlayerId, e.BubbleId, e.Position.X, e.Position.Y, e.RelatedTime); break;
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
        ItemValidation(_script, runId, result.ItemsMatch, result.AttributesMatch, result.ObservationsMatch);
        foreach (var p in world.Players) FinalAttributes(_script, runId, p.Id, p.Speed, p.Capacity, p.Range, p.Vision, p.Pending.Speed, p.Pending.Capacity, p.Pending.Range, p.Pending.Vision);
        for (int i = 0; i < GameSimulation.CellCount; i++) if (world.Items[i].Type != ItemType.None)
            FinalItem(_item, runId, i % GameSimulation.Size, i / GameSimulation.Size, ItemNames.Name(world.Items[i].Type), world.Items[i].Status);
        if (!result.Passed) Failed(_script, runId);
    }

    public void ItemsConfigured(string runId, GameSimulation world)
    {
        for (var type = ItemType.Speed; type <= ItemType.Vision; type++)
            Allocation(_item, runId, world.ItemMode, world.ItemSeed, ItemNames.Name(type), world.RequestedItems.Get(type), world.GeneratedItems.Get(type), world.RequestedItems.Get(type) - world.GeneratedItems.Get(type));
    }
    [LoggerMessage(900, LogLevel.Debug, "ITEM_GENERATED RunId={RunId} Position=({X},{Y}) Type={Type}")]
    private static partial void Generated(ILogger logger, string runId, int x, int y, string type);
    [LoggerMessage(901, LogLevel.Information, "ITEM_EVENT RunId={RunId} RoundId={RoundId} Time={Time} Phase={Phase} Kind={Kind} PlayerId={PlayerId} Position=({X},{Y}) Type={Type} Before={Before} After={After}")]
    private static partial void ItemChanged(ILogger logger, string runId, int roundId, RationalTime time, SimulationPhase phase, EventKind kind, int playerId, int x, int y, string type, int before, int after);
    [LoggerMessage(902, LogLevel.Information, "ITEM_ALLOCATION RunId={RunId} Mode={Mode} Seed={Seed} Type={Type} Requested={Requested} Generated={Generated} Skipped={Skipped}")]
    private static partial void Allocation(ILogger logger, string runId, string mode, int? seed, string type, int requested, int generated, int skipped);
    [LoggerMessage(903, LogLevel.Information, "ITEM_VALIDATION RunId={RunId} ItemsMatch={ItemsMatch} AttributesMatch={AttributesMatch} ObservationsMatch={ObservationsMatch}")]
    private static partial void ItemValidation(ILogger logger, string runId, bool itemsMatch, bool attributesMatch, bool observationsMatch);
    [LoggerMessage(904, LogLevel.Information, "FINAL_ATTRIBUTES RunId={RunId} PlayerId={PlayerId} Speed={Speed} Capacity={Capacity} Range={Range} Vision={Vision} PendingSpeed={PendingSpeed} PendingCapacity={PendingCapacity} PendingRange={PendingRange} PendingVision={PendingVision}")]
    private static partial void FinalAttributes(ILogger logger, string runId, int playerId, int speed, int capacity, int range, int vision, int pendingSpeed, int pendingCapacity, int pendingRange, int pendingVision);
    [LoggerMessage(905, LogLevel.Information, "FINAL_ITEM RunId={RunId} Position=({X},{Y}) Type={Type} Status={Status}")]
    private static partial void FinalItem(ILogger logger, string runId, int x, int y, string type, ItemStatus status);

    [LoggerMessage(100, LogLevel.Information, "SCENARIO_START RunId={RunId} Name={Name}")]
    private static partial void ScriptStarted(ILogger logger, string runId, string name);
    [LoggerMessage(101, LogLevel.Information, "TICK_START RunId={RunId} Tick={Tick}")]
    private static partial void BeginTick(ILogger logger, string runId, int tick);
    [LoggerMessage(102, LogLevel.Information, "TICK_END RunId={RunId} Tick={Tick} Bubbles={Bubbles} Crates={Crates}")]
    private static partial void EndTick(ILogger logger, string runId, int tick, int bubbles, int crates);
    [LoggerMessage(200, LogLevel.Debug, "MOVE RunId={RunId} Tick={Tick} PlayerId={PlayerId} From=({FromX},{FromY}) To=({X},{Y})")]
    private static partial void Movement(ILogger logger, string runId, RationalTime tick, int playerId, int fromX, int fromY, int x, int y);
    [LoggerMessage(201, LogLevel.Debug, "BLOCKED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Target=({X},{Y})")]
    private static partial void Blocked(ILogger logger, string runId, RationalTime tick, int playerId, int x, int y);
    [LoggerMessage(202, LogLevel.Debug, "MOVEMENT_PHASE RunId={RunId} Time={Time} PlayerId={PlayerId} Phase={Phase} Cell=({X},{Y}) Point=({PointX},{PointY})")]
    private static partial void MovementPhase(ILogger logger, string runId, RationalTime time, int playerId, string phase, int x, int y, RationalTime pointX, RationalTime pointY);
    [LoggerMessage(300, LogLevel.Information, "BUBBLE_PLACED RunId={RunId} Tick={Tick} PlayerId={PlayerId} BubbleId={BubbleId} Position=({X},{Y}) DueAt={DueAt}")]
    private static partial void Placed(ILogger logger, string runId, RationalTime tick, int playerId, int bubbleId, int x, int y, RationalTime dueAt);
    [LoggerMessage(302, LogLevel.Information, "BUBBLE_INITIALIZED RunId={RunId} Time={Time} PlayerId={PlayerId} BubbleId={BubbleId} Position=({X},{Y}) DueAt={DueAt}")]
    private static partial void InitialBubble(ILogger logger, string runId, RationalTime time, int playerId, int bubbleId, int x, int y, RationalTime dueAt);
    [LoggerMessage(301, LogLevel.Debug, "PLACEMENT_SKIPPED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y}) Reason={Reason}")]
    private static partial void Skipped(ILogger logger, string runId, RationalTime tick, int playerId, int x, int y, string reason);
    [LoggerMessage(400, LogLevel.Information, "BUBBLE_EXPLODED RunId={RunId} Tick={Tick} PlayerId={PlayerId} BubbleId={BubbleId} Position=({X},{Y}) DueAt={DueAt}")]
    private static partial void Exploded(ILogger logger, string runId, RationalTime tick, int playerId, int bubbleId, int x, int y, RationalTime dueAt);
    [LoggerMessage(403, LogLevel.Information, "BUBBLE_CHAINED RunId={RunId} Tick={Tick} PlayerId={PlayerId} BubbleId={BubbleId} SourceBubbleId={SourceBubbleId} Position=({X},{Y})")]
    private static partial void Chained(ILogger logger, string runId, RationalTime tick, int playerId, int bubbleId, int sourceBubbleId, int x, int y);
    [LoggerMessage(401, LogLevel.Trace, "BLAST_CELL RunId={RunId} Tick={Tick} BubbleId={BubbleId} Position=({X},{Y})")]
    private static partial void Covered(ILogger logger, string runId, RationalTime tick, int bubbleId, int x, int y);
    [LoggerMessage(402, LogLevel.Information, "SAFETY RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y}) Safe={Safe}")]
    private static partial void Safety(ILogger logger, string runId, RationalTime tick, int playerId, int x, int y, bool safe);
    [LoggerMessage(500, LogLevel.Information, "CRATE_DESTROYED RunId={RunId} Tick={Tick} Position=({X},{Y})")]
    private static partial void Destroyed(ILogger logger, string runId, RationalTime tick, int x, int y);
    [LoggerMessage(600, LogLevel.Information, "FINAL_PLAYER RunId={RunId} PlayerId={PlayerId} Position=({X},{Y}) Alive={Alive} DeathTick={DeathTick}")]
    private static partial void FinalPlayer(ILogger logger, string runId, int playerId, int x, int y, bool alive, RationalTime? deathTick);
    [LoggerMessage(601, LogLevel.Information, "VALIDATION RunId={RunId} Passed={Passed} Tick={Tick} Crates={Crates} Walls={Walls} Bubbles={Bubbles} SafePlayers={SafePlayers} Placed={Placed} Exploded={Exploded} Destroyed={Destroyed} MapMatches={MapMatches}")]
    private static partial void Result(ILogger logger, string runId, bool passed, int tick, int crates, int walls, int bubbles, int safePlayers, int placed, int exploded, int destroyed, bool mapMatches);
    [LoggerMessage(602, LogLevel.Error, "VALIDATION_FAILED RunId={RunId}; compare final positions, timing and counters with M0 expectations")]
    private static partial void Failed(ILogger logger, string runId);
    [LoggerMessage(603, LogLevel.Error, "SCENARIO_ERROR RunId={RunId}")]
    private static partial void ScriptError(ILogger logger, Exception exception, string runId);
    [LoggerMessage(604, LogLevel.Information, "FINAL_BUBBLE RunId={RunId} BubbleId={BubbleId} OwnerId={OwnerId} Position=({X},{Y}) PlacedAt={PlacedAt} DueAt={DueAt}")]
    private static partial void FinalBubble(ILogger logger, string runId, int bubbleId, int ownerId, int x, int y, RationalTime placedAt, RationalTime dueAt);
    [LoggerMessage(605, LogLevel.Information, "OUTCOME_VALIDATION RunId={RunId} Ended={Ended} WinnerId={WinnerId} EndTick={EndTick} OutcomeMatches={OutcomeMatches}")]
    private static partial void OutcomeValidation(ILogger logger, string runId, bool ended, int? winnerId, RationalTime? endTick, bool outcomeMatches);
    [LoggerMessage(800, LogLevel.Information, "MATCH_ENDED RunId={RunId} Tick={Tick} WinnerId={WinnerId} Survivors={Survivors}; WinnerId=0 means draw")]
    private static partial void Ended(ILogger logger, string runId, RationalTime tick, int winnerId, int survivors);
    [LoggerMessage(700, LogLevel.Information, "PLAYER_DIED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y}) OwnedBubblesRemaining={OwnedBubbles}")]
    private static partial void Died(ILogger logger, string runId, RationalTime tick, int playerId, int x, int y, int ownedBubbles);
    [LoggerMessage(701, LogLevel.Information, "DEAD_PLACEMENT_CANCELLED RunId={RunId} Tick={Tick} PlayerId={PlayerId} Position=({X},{Y})")]
    private static partial void DeathPlacementCancelled(ILogger logger, string runId, RationalTime tick, int playerId, int x, int y);
    [LoggerMessage(702, LogLevel.Information, "DEAD_ACTION_IGNORED RunId={RunId} Tick={Tick} PlayerId={PlayerId} PlannedRound={PlannedRound}")]
    private static partial void Ignored(ILogger logger, string runId, RationalTime tick, int playerId, int plannedRound);
    [LoggerMessage(703, LogLevel.Information, "DEATH_VALIDATION RunId={RunId} DeadPlayers={DeadPlayers} Deaths={Deaths} PlayersMatch={PlayersMatch} BubblesMatch={BubblesMatch} ActionsMatch={ActionsMatch}")]
    private static partial void DeathValidation(ILogger logger, string runId, int deadPlayers, int deaths, bool playersMatch, bool bubblesMatch, bool actionsMatch);
}
