using System.Text.Json;
using QQT.Core;

namespace QQT.Server;

public sealed partial class BattleCoordinator
{
    private JsonMessageBuffer Begin(string type, JsonElement? request = null)
    {
        if (request is { ValueKind: not JsonValueKind.Object }) request = null;
        var message = _messages.Get(); var w = message.Writer;
        w.WriteStartObject(); w.WriteString("type", type);
        w.WriteString("gameId", request is { } r && r.TryGetProperty("gameId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : _gameId);
        if (request is { } root)
        {
            if (root.TryGetProperty("roundId", out var round)) { w.WritePropertyName("roundId"); round.WriteTo(w); }
            if (root.TryGetProperty("requestId", out var id)) { w.WritePropertyName("requestId"); id.WriteTo(w); }
        }
        else w.WriteNumber("roundId", _phase == "simulating" || _phase == "ended" ? _world.Tick : _world.Tick + 1);
        w.WriteStartObject("payload"); return message;
    }
    private void End(BattlePeer peer, JsonMessageBuffer message)
    {
        var w = message.Writer; w.WriteEndObject(); w.WriteEndObject(); w.Flush();
        try { Record(message.Written, peer.IsUi ? "uiState" : "outgoing", peer.PlayerId); }
        catch { _messages.Return(message); throw; }
        peer.Send(message);
    }
    private static void WriteError(Utf8JsonWriter w, string? code, string? text)
    {
        if (code is null) { w.WriteNull("error"); return; }
        w.WriteStartObject("error"); w.WriteString("code", code); w.WriteString("message", text ?? code); w.WriteEndObject();
    }
    private void SendFailure(BattlePeer peer, JsonElement? root, string code, string text)
    {
        var message = Begin("error", root); WriteError(message.Writer, code, text); End(peer, message);
    }
    private string? Check(BattlePeer peer, JsonElement root)
    {
        if (root.GetProperty("gameId").GetString() != _gameId || peer.GameId != _gameId) return "wrongGame";
        if (root.GetProperty("roundId").GetInt32() != _world.Tick + 1) return "wrongRound";
        if (peer.PlayerId < 1 || !ReferenceEquals(_ai[peer.PlayerId - 1], peer)) return "notRegistered";
        if (!_world.Players[peer.PlayerId - 1].Alive) return "eliminated";
        if (_phase != "collecting") return "wrongStage";
        return null;
    }
    private static bool InView(in Player player, Position point) => Math.Abs((long)point.X - player.Position.X) <= player.Vision && Math.Abs((long)point.Y - player.Position.Y) <= player.Vision;
    private int FrozenOwned(int playerId)
    {
        int count = 0; for (int b = 0; b < _frozenBubbleCount; b++) if (_frozenBubbles[b].OwnerId == playerId) count++; return count;
    }
    private static void WritePlayerDetails(Utf8JsonWriter w, in Player p, int owned)
    {
        w.WriteNumber("playerId", p.Id); WorldFrame.WritePosition(w, "position", p.Position);
        w.WriteNumber("speed", p.Speed); w.WriteNumber("bubbleCapacity", p.Capacity); w.WriteNumber("blastRange", p.Range);
        w.WriteNumber("visionWidth", checked(2L * p.Vision + 1)); w.WriteNumber("activeBubbleCount", owned);
    }
    private void SendObservation(BattlePeer peer, PlayerObservation observation)
    {
        var message = Begin("round.started"); var w = message.Writer; var p = observation.Self;
        WorldFrame.WriteTime(w, "time", observation.Time);
        w.WriteStartObject("self"); w.WriteNumber("playerId", p.Id); WorldFrame.WritePosition(w, "position", p.Position);
        w.WriteNumber("speed", p.Speed); w.WriteNumber("bubbleCapacity", p.Capacity); w.WriteNumber("activeBubbleCount", FrozenOwned(p.Id));
        w.WriteNumber("blastRange", p.Range); WorldFrame.WriteTime(w, "bubbleLifetime", p.Lifetime);
        w.WriteNumber("visionRadius", p.Vision); w.WriteNumber("actionPoints", _budgets[p.Id - 1]); w.WriteEndObject();
        w.WriteStartArray("participantIds");
        for (int i = 0; i < _world.PlayerCount; i++) if (_frozenPlayers[i].Alive && i + 1 != p.Id && !InView(p, _frozenPlayers[i].Position)) w.WriteNumberValue(i + 1);
        w.WriteEndArray(); w.WriteStartObject("visible");
        w.WriteStartArray("cells"); foreach (var c in observation.Cells)
        {
            w.WriteStartObject(); WorldFrame.WritePosition(w, "position", c.Position); w.WriteString("terrain", c.Terrain switch { Cell.Wall => "wall", Cell.Crate => "crate", _ => "empty" }); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("players"); foreach (var other in observation.Players)
        {
            w.WriteStartObject(); w.WriteNumber("playerId", other.Id); WorldFrame.WritePosition(w, "position", other.Position); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("bubbles"); foreach (var b in observation.Bubbles)
        {
            w.WriteStartObject(); w.WriteNumber("queryId", b.QueryId); WorldFrame.WritePosition(w, "position", b.Position); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("items"); foreach (var item in observation.Items)
        {
            w.WriteStartObject(); WorldFrame.WritePosition(w, "position", item.Position); w.WriteString("type", ItemNames.Name(item.Type)); w.WriteNumber("effect", item.Effect); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject(); End(peer, message);
    }
    private void Query(BattlePeer peer, JsonElement root)
    {
        var payload = root.GetProperty("payload"); string targetType = payload.GetProperty("targetType").GetString() ?? "";
        int targetId = payload.GetProperty("targetId").GetInt32(); int index = peer.PlayerId - 1;
        string? error = Check(peer, root); int cost = 0; Bubble bubble = default;
        if (error is null && _locked[index]) error = "planLocked";
        if (error is null)
        {
            if (targetType == "player")
            {
                if (targetId < 1 || targetId > _world.PlayerCount || targetId == peer.PlayerId) error = "invalidTarget";
                else if (!_world.Players[targetId - 1].Alive) error = "targetEliminated";
                else cost = InView(_frozenPlayers[index], _frozenPlayers[targetId - 1].Position) ? _settings.VisiblePlayerCost : _settings.OutsidePlayerCost;
            }
            else if (targetType == "bubble")
            {
                int queryId = 0;
                for (int b = 0; b < _frozenBubbleCount; b++) if (InView(_frozenPlayers[index], _frozenBubbles[b].Position))
                {
                    queryId++; if (queryId == targetId) { bubble = _frozenBubbles[b]; cost = _settings.BubbleCost; break; }
                }
                if (bubble.Id == 0) error = "invalidTarget";
            }
            else error = "invalidTargetType";
            if (error is null && _budgets[index] < cost) error = "insufficientActionPoints";
        }
        if (error is not null) cost = 0; else _budgets[index] -= cost;
        var message = Begin("query.result", root); var w = message.Writer;
        w.WriteString("targetType", targetType); w.WriteNumber("targetId", targetId); w.WriteBoolean("success", error is null);
        w.WriteNumber("cost", cost); w.WriteNumber("actionPoints", index >= 0 ? _budgets[index] : 0);
        if (error is not null) w.WriteNull("data");
        else
        {
            w.WriteStartObject("data");
            if (targetType == "player") WritePlayerDetails(w, _frozenPlayers[targetId - 1], FrozenOwned(targetId));
            else { w.WriteNumber("ownerId", bubble.OwnerId); w.WriteNumber("blastRange", _frozenPlayers[bubble.OwnerId - 1].Range); WorldFrame.WriteTime(w, "dueAt", bubble.DueAt); }
            w.WriteEndObject();
        }
        WriteError(w, error, null); End(peer, message);
        if (_log.IsEnabled(LogLevel.Debug)) LogQuery(_log, _gameId, _world.Tick + 1, peer.PlayerId,
            root.GetProperty("requestId").GetString()!, targetType, targetId, cost, index >= 0 ? _budgets[index] : 0, error);
    }
    private void Submit(BattlePeer peer, JsonElement root)
    {
        string? error = Check(peer, root); int index = peer.PlayerId - 1;
        if (error is null && !_locked[index])
        {
            try
            {
                var payload = root.GetProperty("payload");
                var direction = payload.GetProperty("direction").GetString() switch
                {
                    "wait" => Direction.Wait, "up" => Direction.Up, "down" => Direction.Down, "left" => Direction.Left, "right" => Direction.Right,
                    _ => throw new InvalidDataException("方向非法。")
                };
                var placements = payload.GetProperty("placements"); int count = placements.GetArrayLength();
                if (count > GameSimulation.Size) throw new InvalidDataException("放置数量非法。");
                int k = 0; foreach (var p in placements.EnumerateArray()) _placements[index * GameSimulation.Size + k++] = new Position(p.GetProperty("x").GetInt32(), p.GetProperty("y").GetInt32());
                var plan = new PlayerAction(peer.PlayerId, direction, null) { Placements = _placements.AsMemory(index * GameSimulation.Size, count) };
                _world.ValidatePlan(plan); _plans[index] = plan; _locked[index] = true;
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { error = "invalidPlan"; }
        }
        var message = Begin("action.result", root); var w = message.Writer;
        bool locked = error is null && _locked[index]; w.WriteBoolean("success", error is null); w.WriteBoolean("locked", locked);
        if (!locked) w.WriteNull("plan"); else WritePlan(w, _plans[index]);
        WriteError(w, error, null); End(peer, message);
        if (_log.IsEnabled(LogLevel.Debug)) LogPlan(_log, _gameId, _world.Tick + 1, peer.PlayerId,
            root.GetProperty("requestId").GetString()!, locked, error);
        PublishState(); ScheduleSimulation();
    }
    private static void WritePlan(Utf8JsonWriter w, in PlayerAction plan)
    {
        w.WriteStartObject("plan"); w.WriteString("direction", plan.Direction switch { Direction.Up => "up", Direction.Down => "down", Direction.Left => "left", Direction.Right => "right", _ => "wait" }); w.WriteStartArray("placements");
        for (int k = 0; k < plan.PlacementCount; k++)
        {
            var p = plan.PlacementAt(k); w.WriteStartObject(); w.WriteNumber("x", p.X); w.WriteNumber("y", p.Y); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
}
