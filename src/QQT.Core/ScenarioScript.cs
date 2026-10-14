using System.Buffers;
using System.Text.Json;

namespace QQT.Core;

/// <summary>有限轮次的四人验证脚本；计划和 JSON 预期由本对象持有并在运行结束归还。</summary>
public sealed class ScenarioScript : IDisposable
{
    private PlayerAction[]? _actions;
    private JsonDocument? _document;
    public string Name { get; private set; } = "M0";
    public int TotalTicks { get; }
    public JsonElement Expected => _document?.RootElement.GetProperty("expected") ?? throw new ObjectDisposedException(nameof(ScenarioScript));

    private ScenarioScript(int totalTicks)
    {
        TotalTicks = totalTicks;
        _actions = ArrayPool<PlayerAction>.Shared.Rent(totalTicks * 4);
    }

    public static ScenarioScript Load(string path, int maxTicks)
    {
        using var stream = File.OpenRead(path);
        var document = JsonDocument.Parse(stream);
        ScenarioScript? script = null;
        try
        {
            var root = document.RootElement;
            int totalTicks = root.GetProperty("totalTicks").GetInt32();
            if (totalTicks < 1 || totalTicks > maxTicks)
                throw new InvalidDataException($"脚本轮数必须在 1~{maxTicks} 之间。");
            script = new ScenarioScript(totalTicks) { _document = document };
            script._actions!.AsSpan(0, totalTicks * 4).Clear();
            script.Name = root.GetProperty("name").GetString() ?? "M0";
            var rounds = root.GetProperty("rounds");
            if (rounds.GetArrayLength() != totalTicks)
                throw new InvalidDataException("脚本轮数与 totalTicks 不一致。");
            for (int r = 0; r < totalTicks; r++)
            {
                var round = rounds[r];
                if (round.GetProperty("roundId").GetInt32() != r + 1)
                    throw new InvalidDataException("脚本轮次必须连续且从 1 开始。");
                var actions = round.GetProperty("actions");
                if (actions.GetArrayLength() != 4)
                    throw new InvalidDataException($"第 {r + 1} 轮缺少四人完整动作。");
                for (int a = 0; a < 4; a++)
                {
                    var json = actions[a];
                    int id = json.GetProperty("playerId").GetInt32();
                    if (id is < 1 or > 4 || script._actions![r * 4 + id - 1].PlayerId != 0)
                        throw new InvalidDataException("玩家编号非法或重复。");
                    var direction = json.GetProperty("direction").GetString() switch
                    {
                        "wait" => Direction.Wait, "up" => Direction.Up, "down" => Direction.Down,
                        "left" => Direction.Left, "right" => Direction.Right,
                        _ => throw new InvalidDataException("移动方向非法。")
                    };
                    var placements = json.GetProperty("placements");
                    if (placements.GetArrayLength() > 1)
                        throw new InvalidDataException("M0 每次动作最多选择一个放置点。");
                    Position? placement = null;
                    if (placements.GetArrayLength() == 1) placement = ReadPosition(placements[0]);
                    script._actions![r * 4 + id - 1] = new(id, direction, placement);
                }
            }
            script.CheckExpected();
            return script;
        }
        catch
        {
            if (script is null) document.Dispose(); else script.Dispose();
            throw;
        }
    }

    private void CheckExpected()
    {
        var expected = Expected;
        var players = expected.GetProperty("players");
        if (players.GetArrayLength() != 4) throw new InvalidDataException("预期结果需要四个玩家，按编号排列。");
        for (int i = 0; i < 4; i++)
        {
            var player = players[i];
            if (player.GetProperty("playerId").GetInt32() != i + 1) throw new InvalidDataException("预期玩家必须按编号排列。");
            ReadPosition(player.GetProperty("position"));
            var death = player.GetProperty("deathTick");
            if (death.ValueKind != JsonValueKind.Null && (death.GetInt32() < 1 || death.GetInt32() > TotalTicks))
                throw new InvalidDataException("预期死亡时刻越界。");
            if (player.GetProperty("ownedBubblesAtDeath").GetInt32() < 0) throw new InvalidDataException("死亡时泡泡数非法。");
        }
        int id = 0;
        foreach (var bubble in expected.GetProperty("bubbles").EnumerateArray())
        {
            if (bubble.GetProperty("id").GetInt32() != ++id || id > TotalTicks * 4) throw new InvalidDataException("预期泡泡编号非法。");
            int owner = bubble.GetProperty("ownerId").GetInt32();
            if (owner is < 1 or > 4) throw new InvalidDataException("预期泡泡归属非法。");
            ReadPosition(bubble.GetProperty("position"));
            int placed = bubble.GetProperty("placedAt").GetInt32();
            int? exploded = bubble.GetProperty("explodedAt").ValueKind == JsonValueKind.Null ? null : bubble.GetProperty("explodedAt").GetInt32();
            if (placed < 0 || placed > TotalTicks || exploded < placed || exploded > TotalTicks) throw new InvalidDataException("预期泡泡时间非法。");
            if (bubble.TryGetProperty("dueAt", out var due) && due.GetInt32() != placed + 3) throw new InvalidDataException("预定到期必须等于实际放置时刻加 3。");
            if (bubble.TryGetProperty("sourceBubbleId", out var source) && source.ValueKind != JsonValueKind.Null &&
                (source.GetInt32() < 1 || source.GetInt32() > expected.GetProperty("bubbles").GetArrayLength() || source.GetInt32() == id || exploded is null))
                throw new InvalidDataException("预期连锁来源非法。");
        }
        if (expected.TryGetProperty("end", out var end))
        {
            int tick = end.GetProperty("tick").GetInt32();
            var winner = end.GetProperty("winnerId");
            if (tick < 1 || tick > TotalTicks || (winner.ValueKind != JsonValueKind.Null && winner.GetInt32() is < 1 or > 4))
                throw new InvalidDataException("预期结束时刻或胜者非法。");
            int survivors = 0, survivorId = 0;
            foreach (var player in players.EnumerateArray())
            {
                var death = player.GetProperty("deathTick");
                if (death.ValueKind == JsonValueKind.Null) { survivors++; survivorId = player.GetProperty("playerId").GetInt32(); }
                else if (death.GetInt32() > tick) throw new InvalidDataException("预期死亡不能晚于结束。");
            }
            if ((winner.ValueKind == JsonValueKind.Null && survivors != 0) ||
                (winner.ValueKind != JsonValueKind.Null && (survivors != 1 || survivorId != winner.GetInt32())))
                throw new InvalidDataException("预期胜者与存活玩家不一致。");
            foreach (var bubble in expected.GetProperty("bubbles").EnumerateArray())
                if (bubble.GetProperty("placedAt").GetInt32() > tick ||
                    (bubble.GetProperty("explodedAt").ValueKind != JsonValueKind.Null && bubble.GetProperty("explodedAt").GetInt32() > tick))
                    throw new InvalidDataException("预期泡泡事件不能晚于结束。");
        }
        foreach (var point in expected.GetProperty("destroyedCrates").EnumerateArray()) ReadPosition(point);
        if (expected.GetProperty("deadPlacementSkips").GetInt32() < 0 || expected.GetProperty("ignoredDeadActions").GetInt32() < 0)
            throw new InvalidDataException("预期取消次数不能为负。");
    }

    internal static Position ReadPosition(JsonElement json)
    {
        var position = new Position(json.GetProperty("x").GetInt32(), json.GetProperty("y").GetInt32());
        if (!position.InBounds) throw new InvalidDataException("坐标越界。");
        return position;
    }

    public ReadOnlySpan<PlayerAction> Round(int tick)
    {
        ObjectDisposedException.ThrowIf(_actions is null, this);
        if (tick < 1 || tick > TotalTicks) throw new ArgumentOutOfRangeException(nameof(tick));
        return _actions.AsSpan((tick - 1) * 4, 4);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _actions, null);
        if (buffer is not null) ArrayPool<PlayerAction>.Shared.Return(buffer);
        Interlocked.Exchange(ref _document, null)?.Dispose();
    }
}
