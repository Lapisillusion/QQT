using System.Buffers;
using System.Text.Json;

namespace QQT.Core;

/// <summary>仅解析 M0 的四轮四人计划；数组所有权由脚本持有，运行结束归还。</summary>
public sealed class ScenarioScript : IDisposable
{
    private PlayerAction[]? _actions;
    public string Name { get; private set; } = "M0";
    public int TotalTicks => 4;

    private ScenarioScript() => _actions = ArrayPool<PlayerAction>.Shared.Rent(16);

    public static ScenarioScript Load(string path)
    {
        var script = new ScenarioScript();
        try
        {
            script._actions!.AsSpan(0, 16).Clear();
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            script.Name = root.GetProperty("name").GetString() ?? "M0";
            if (root.GetProperty("totalTicks").GetInt32() != 4)
                throw new InvalidDataException("M0 脚本必须明确执行四轮。");
            var rounds = root.GetProperty("rounds");
            if (rounds.GetArrayLength() != 4)
                throw new InvalidDataException("M0 脚本必须包含四轮。");
            for (int r = 0; r < 4; r++)
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
                    if (placements.GetArrayLength() == 1)
                    {
                        var point = placements[0];
                        var position = new Position(point.GetProperty("x").GetInt32(), point.GetProperty("y").GetInt32());
                        if (!position.InBounds) throw new InvalidDataException("放置坐标越界。");
                        placement = position;
                    }
                    script._actions![r * 4 + id - 1] = new(id, direction, placement);
                }
            }
            return script;
        }
        catch
        {
            script.Dispose();
            throw;
        }
    }

    public ReadOnlySpan<PlayerAction> Round(int tick)
    {
        ObjectDisposedException.ThrowIf(_actions is null, this);
        if (tick is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(tick));
        return _actions.AsSpan((tick - 1) * 4, 4);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _actions, null);
        if (buffer is not null) ArrayPool<PlayerAction>.Shared.Return(buffer);
    }
}
