using System.Buffers;
using System.Text.Json;

namespace QQT.Core;

/// <summary>有限轮次的四人验证脚本；计划和 JSON 预期由本对象持有并在运行结束归还。</summary>
public sealed class ScenarioScript : IDisposable
{
    private PlayerAction[]? _actions;
    private Position[]? _placements;
    private JsonDocument? _document;
    public string Name { get; private set; } = "M0";
    public int TotalTicks { get; }
    public JsonElement? Initial => _document!.RootElement.TryGetProperty("initial", out var initial) ? initial : null;
    public JsonElement? Items => _document!.RootElement.TryGetProperty("items", out var items) ? items : null;
    public JsonElement Expected => _document?.RootElement.GetProperty("expected") ?? throw new ObjectDisposedException(nameof(ScenarioScript));

    private ScenarioScript(int totalTicks)
    {
        TotalTicks = totalTicks;
        _actions = ArrayPool<PlayerAction>.Shared.Rent(totalTicks * 4);
        try { _placements = ArrayPool<Position>.Shared.Rent(checked(totalTicks * 4 * GameSimulation.Size)); }
        catch { ArrayPool<PlayerAction>.Shared.Return(_actions, true); _actions = null; throw; }
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
                    int count = placements.GetArrayLength();
                    if (count > GameSimulation.Size) throw new InvalidDataException("放置点超过直线路径的格数。");
                    int offset = (r * 4 + id - 1) * GameSimulation.Size;
                    for (int k = 0; k < count; k++)
                    {
                        var point = ReadPosition(placements[k]);
                        for (int j = 0; j < k; j++) if (script._placements![offset + j] == point) throw new InvalidDataException("同轮放置点重复。");
                        script._placements![offset + k] = point;
                    }
                    script._actions![r * 4 + id - 1] = new(id, direction, null) { Placements = script._placements.AsMemory(offset, count) };
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
        if (players.GetArrayLength() != 4) throw new InvalidDataException("预期必须声明四人。");
        for (int i = 0; i < 4; i++)
        {
            var row = players[i]; ReadPosition(row.GetProperty("position"));
            if (row.GetProperty("playerId").GetInt32() != i + 1) throw new InvalidDataException("预期玩家按编号排列。");
            if (row.GetProperty("deathTick").ValueKind != JsonValueKind.Null)
            {
                var time = RationalTime.Read(row.GetProperty("deathTick"));
                if (time < 0 || time > TotalTicks) throw new InvalidDataException("预期死亡时刻非法。");
            }
        }
        if (expected.TryGetProperty("observations", out var observations))
            foreach (var row in observations.EnumerateArray())
            {
                int round = row.GetProperty("roundId").GetInt32();
                if (round < 1 || round > TotalTicks || row.GetProperty("playerId").GetInt32() is < 1 or > 4)
                    throw new InvalidDataException("观察断言的轮次或玩家非法。");
            }
        int id = 0;
        foreach (var bubble in expected.GetProperty("bubbles").EnumerateArray())
        {
            if (bubble.GetProperty("id").GetInt32() != ++id || bubble.GetProperty("ownerId").GetInt32() is < 1 or > 4) throw new InvalidDataException("预期泡泡编号或归属非法。");
            ReadPosition(bubble.GetProperty("position"));
            var placed = RationalTime.Read(bubble.GetProperty("placedAt"));
            bool initial = bubble.TryGetProperty("initial", out var init) && init.GetBoolean();
            if ((!initial && placed < 0) || placed > TotalTicks) throw new InvalidDataException("预期放置时刻非法。");
            if (bubble.GetProperty("explodedAt").ValueKind != JsonValueKind.Null)
            {
                var at = RationalTime.Read(bubble.GetProperty("explodedAt"));
                if (at < 0 || at < placed || at > TotalTicks) throw new InvalidDataException("预期引爆时刻非法。");
            }
            if (bubble.TryGetProperty("dueAt", out var due) && RationalTime.Read(due) < placed) throw new InvalidDataException("预期到期早于放置。");
        }
        if (expected.TryGetProperty("end", out var end))
        {
            var at = RationalTime.Read(end.GetProperty("tick"));
            if (at < 0 || at > TotalTicks || (end.GetProperty("winnerId").ValueKind != JsonValueKind.Null && end.GetProperty("winnerId").GetInt32() is < 1 or > 4)) throw new InvalidDataException("预期终局非法。");
        }
        foreach (var point in expected.GetProperty("destroyedCrates").EnumerateArray()) ReadPosition(point);
        if (expected.GetProperty("deadPlacementSkips").GetInt32() < 0 || expected.GetProperty("ignoredDeadActions").GetInt32() < 0) throw new InvalidDataException("预期计数不能为负。");
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
        if (buffer is not null) ArrayPool<PlayerAction>.Shared.Return(buffer, true);
        var placements = Interlocked.Exchange(ref _placements, null);
        if (placements is not null) ArrayPool<Position>.Shared.Return(placements);
        Interlocked.Exchange(ref _document, null)?.Dispose();
    }
}
