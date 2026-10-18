using System.Text.Json;
using QQT.Core;
namespace QQT.Server;

internal static class WorldFrame
{
    public static void Write(Utf8JsonWriter w, GameSimulation _world, ReadOnlySpan<SimulationEvent> _history, int _historyCount, string _runId, string _status, string _error, string _scenarioName, int _totalTicks, ValidationResult? _result, int animationMilliseconds, bool animate, int catchUpThreshold, double playbackSpeed)
    {
        w.WriteStartObject();
        w.WriteString("type", "frame"); w.WriteString("runId", _runId); w.WriteString("status", _status);
        w.WriteString("error", _error); w.WriteString("name", _scenarioName); w.WriteNumber("totalTicks", _totalTicks); w.WriteNumber("tick", _world.Tick); w.WriteNumber("size", GameSimulation.Size);
        WriteTime(w, "time", _world.Time); w.WriteNumber("durationMs", animationMilliseconds); w.WriteBoolean("animate", animate);
        w.WriteNumber("catchUpThreshold", catchUpThreshold); w.WriteNumber("playbackSpeed", playbackSpeed);
        w.WriteBoolean("ended", _world.Ended);
        if (_world.WinnerId is { } winner) w.WriteNumber("winnerId", winner); else w.WriteNull("winnerId");
        if (_world.EndTick is { } endedAt) WriteTime(w, "endTick", endedAt); else w.WriteNull("endTick");
        w.WriteNumber("crateCount", _world.CrateCount); w.WriteNumber("bubbleCount", _world.BubbleCount);
        w.WriteStartArray("terrain");
        foreach (var cell in _world.Terrain) w.WriteNumberValue((int)cell);
        w.WriteEndArray();
        w.WriteStartArray("players");
        foreach (var p in _world.Players)
        {
            w.WriteStartObject(); w.WriteNumber("id", p.Id); WritePosition(w, "position", p.Position); w.WriteBoolean("hit", p.Hit); w.WriteBoolean("alive", p.Alive);
            if (p.DeathTick is { } diedAt) WriteTime(w, "deathTick", diedAt); else w.WriteNull("deathTick");
            WritePoint(w, "point", p.Point); w.WriteNumber("speed", p.Speed); w.WriteNumber("capacity", p.Capacity); w.WriteNumber("range", p.Range); WriteTime(w, "lifetime", p.Lifetime); w.WriteNumber("vision", p.Vision); WriteGains(w, "pending", p.Pending); w.WriteEndObject();
        }
        w.WriteEndArray(); WriteItems(w, _world); w.WriteStartArray("bubbles");
        foreach (var b in _world.Bubbles)
        {
            w.WriteStartObject(); w.WriteNumber("id", b.Id); w.WriteNumber("ownerId", b.OwnerId); WritePosition(w, "position", b.Position);
            WriteTime(w, "placedAt", b.PlacedAt); WriteTime(w, "dueAt", b.DueAt); w.WriteNumber("range", _world.Players[b.OwnerId - 1].Range); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("moves");
        if (animate) foreach (var m in _world.Moves)
        {
            w.WriteStartObject(); w.WriteNumber("playerId", m.PlayerId); WritePoint(w, "from", m.From); WritePoint(w, "to", m.To); WriteTime(w, "start", m.Start); WriteTime(w, "end", m.End); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("events");
        for (int i = 0; i < _historyCount; i++)
        {
            var e = _history[i]; if (e.Kind == EventKind.State) continue; WriteEvent(w, e, i + 1);
        }
        w.WriteEndArray();
        if (_result is { } r)
        {
            w.WriteStartObject("result"); w.WriteBoolean("passed", r.Passed); w.WriteNumber("safePlayers", r.SafePlayers);
            w.WriteNumber("walls", r.Walls); w.WriteNumber("placed", r.Placed); w.WriteNumber("exploded", r.Exploded);
            w.WriteNumber("destroyed", r.Destroyed); w.WriteBoolean("mapMatches", r.MapMatches); w.WriteNumber("deadPlayers", r.DeadPlayers);
            w.WriteNumber("deaths", r.Deaths); w.WriteBoolean("playersMatch", r.PlayersMatch); w.WriteBoolean("bubblesMatch", r.BubblesMatch); w.WriteBoolean("actionsMatch", r.ActionsMatch); w.WriteBoolean("outcomeMatches", r.OutcomeMatches); w.WriteBoolean("itemsMatch", r.ItemsMatch); w.WriteBoolean("attributesMatch", r.AttributesMatch); w.WriteBoolean("observationsMatch", r.ObservationsMatch); w.WriteEndObject();
        }
        w.WriteEndObject(); w.Flush();
    }
    internal static void WriteEvent(Utf8JsonWriter w, in SimulationEvent e, int sequence)
    {
        w.WriteStartObject(); w.WriteNumber("sequence", sequence); w.WriteString("kind", EventName(e.Kind));
            WriteTime(w, "tick", e.Tick); w.WriteNumber("roundId", e.RoundId); WritePoint(w, "point", e.Point); WriteTime(w, "relatedTime", e.RelatedTime); w.WriteNumber("playerId", e.PlayerId); w.WriteNumber("bubbleId", e.BubbleId);
            WritePosition(w, "position", e.Position); WritePosition(w, "from", e.From); w.WriteBoolean("success", e.Success);
            w.WriteString("itemType", ItemNames.Name(e.ItemType)); w.WriteNumber("previousValue", e.PreviousValue);
            w.WriteString("phase", e.Phase switch { SimulationPhase.Initialization => "initialization", SimulationPhase.RoundStart => "roundStart", _ => "execution" }); WriteAttributes(w, e.Attributes); WriteGains(w, "pending", e.Pending);
            w.WriteNumber("value", e.Value); w.WriteString("reason", e.Reason switch { SkipReason.Dead => "dead", SkipReason.NotReached => "notReached", SkipReason.Capacity => "capacity", _ => "none" }); w.WriteEndObject();
    }
    internal static void WriteTime(Utf8JsonWriter w, string name, RationalTime time) { w.WritePropertyName(name); time.Write(w); }
    internal static void WritePoint(Utf8JsonWriter w, string name, ExactPosition point)
    {
        w.WriteStartObject(name); WriteTime(w, "x", point.X); WriteTime(w, "y", point.Y); w.WriteEndObject();
    }
    internal static void WritePosition(Utf8JsonWriter w, string name, Position p)
    {
        w.WriteStartObject(name); w.WriteNumber("x", p.X); w.WriteNumber("y", p.Y); w.WriteEndObject();
    }
    internal static string EventName(EventKind kind) => kind switch
    {
        EventKind.ItemGenerated => "itemGenerated", EventKind.ItemExposed => "itemExposed", EventKind.ItemDestroyed => "itemDestroyed",
        EventKind.ItemPicked => "itemPicked", EventKind.AttributeActivated => "attributeActivated",
        EventKind.State => "state", EventKind.InitialBubble => "initialBubble", EventKind.Entered => "entered", EventKind.ReturnStarted => "returnStarted", EventKind.Returned => "returned", EventKind.Move => "move", EventKind.Blocked => "blocked", EventKind.Placed => "placed",
        EventKind.Exploded => "exploded", EventKind.Covered => "covered", EventKind.CrateDestroyed => "crateDestroyed",
        EventKind.Disconnected => "disconnected", EventKind.Chained => "chained", EventKind.MatchEnded => "matchEnded",
        EventKind.Safety => "safety", EventKind.Death => "death", EventKind.ActionIgnored => "actionIgnored", _ => "placementSkipped"
    };
    internal static void WriteGains(Utf8JsonWriter w, string name, ItemGains gains)
    {
        w.WriteStartObject(name);
        for (var type = ItemType.Speed; type <= ItemType.Vision; type++) w.WriteNumber(ItemNames.Name(type), gains.Get(type));
        w.WriteEndObject();
    }
    internal static void WriteAttributes(Utf8JsonWriter w, PlayerAttributes values)
    {
        w.WriteStartObject("attributes"); w.WriteNumber("speed", values.Speed); w.WriteNumber("capacity", values.Capacity);
        w.WriteNumber("range", values.Range); w.WriteNumber("vision", values.Vision); w.WriteEndObject();
    }
    internal static void WriteItems(Utf8JsonWriter w, GameSimulation _world)
    {
        w.WriteStartArray("items");
        for (int i = 0; i < GameSimulation.CellCount; i++)
        {
            var item = _world.Items[i]; if (item.Type == ItemType.None) continue;
            w.WriteStartObject(); w.WriteNumber("id", i + 1); WritePosition(w, "position", new(i % GameSimulation.Size, i / GameSimulation.Size));
            w.WriteString("type", ItemNames.Name(item.Type)); w.WriteString("status", item.Status switch { ItemStatus.Hidden => "hidden", ItemStatus.Exposed => "exposed", ItemStatus.Destroyed => "destroyed", ItemStatus.Picked => "picked", _ => "none" }); w.WriteNumber("effect", 1); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartObject("itemAllocation"); w.WriteString("mode", _world.ItemMode);
        if (_world.ItemSeed is { } seed) w.WriteNumber("seed", seed); else w.WriteNull("seed");
        WriteGains(w, "requested", _world.RequestedItems); WriteGains(w, "generated", _world.GeneratedItems);
        WriteGains(w, "skipped", new(_world.RequestedItems.Speed - _world.GeneratedItems.Speed, _world.RequestedItems.Capacity - _world.GeneratedItems.Capacity,
            _world.RequestedItems.Range - _world.GeneratedItems.Range, _world.RequestedItems.Vision - _world.GeneratedItems.Vision));
        w.WriteEndObject();
    }
}
