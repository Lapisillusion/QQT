using System.Text.Json;

namespace QQT.Core;

public sealed partial class GameSimulation
{
    /// <summary>在 PrepareRound 后捕获本轮观察；不修改世界。宿主串行调用，调用者负责归还。</summary>
    public PlayerObservation Observe(int playerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_roundPrepared || Ended) throw new InvalidOperationException("只能在新轮准备后生成冻结观察。");
        if (playerId < 1 || playerId > PlayerCount || !_players[playerId - 1].Alive) throw new InvalidDataException("仅存活玩家可获取观察。");
        return new PlayerObservation(this, playerId);
    }

    /// <summary>脚本在该轮 Advance 前调用；只读观察断言的累计结果归入最终验收。</summary>
    public void ValidateObservations(JsonElement expected)
    {
        if (!expected.TryGetProperty("observations", out var rows)) return;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("roundId").GetInt32() != Tick + 1) continue;
            _checkedObservationCount++;
            using var observation = Observe(row.GetProperty("playerId").GetInt32());
            bool same = (!row.TryGetProperty("cellCount", out var count) || observation.Cells.Length == count.GetInt32()) &&
                (!row.TryGetProperty("vision", out var radius) || observation.Self.Vision == radius.GetInt32());
            if (row.TryGetProperty("self", out var self)) same &= MatchAttributes(self, PlayerAttributes.From(observation.Self), observation.Self.Pending);
            if (row.TryGetProperty("items", out var items))
            {
                same &= items.GetArrayLength() == observation.Items.Length;
                foreach (var visible in observation.Items)
                {
                    int matches = 0;
                    foreach (var item in items.EnumerateArray())
                        if (visible.Position == ScenarioScript.ReadPosition(item.GetProperty("position")) && visible.Type == ItemNames.Parse(item.GetProperty("type").GetString()) && visible.Effect == 1) matches++;
                    same &= matches == 1; // 重复的预期行不能掩盖另一件实际可见道具。
                }
            }
            if (row.TryGetProperty("cells", out var cells))
                foreach (var cell in cells.EnumerateArray())
                {
                    var position = ScenarioScript.ReadPosition(cell.GetProperty("position")); bool found = false;
                    foreach (var visible in observation.Cells)
                        if (visible.Position == position && visible.Terrain == Enum.Parse<Cell>(cell.GetProperty("terrain").GetString()!, true)) found = true;
                    same &= found;
                }
            if (row.TryGetProperty("absentCells", out var absent))
                foreach (var cell in absent.EnumerateArray())
                {
                    var position = ScenarioScript.ReadPosition(cell);
                    foreach (var visible in observation.Cells) same &= visible.Position != position;
                }
            if (row.TryGetProperty("players", out var players))
            {
                same &= players.GetArrayLength() == observation.Players.Length;
                foreach (var player in players.EnumerateArray())
                {
                    bool found = false;
                    foreach (var visible in observation.Players)
                        if (visible.Id == player.GetProperty("playerId").GetInt32() && visible.Position == ScenarioScript.ReadPosition(player.GetProperty("position"))) found = true;
                    same &= found;
                }
            }
            if (row.TryGetProperty("bubbles", out var bubbles))
            {
                same &= bubbles.GetArrayLength() == observation.Bubbles.Length;
                foreach (var bubble in bubbles.EnumerateArray())
                {
                    bool found = false;
                    foreach (var visible in observation.Bubbles)
                        if (visible.QueryId == bubble.GetProperty("queryId").GetInt32() && visible.Position == ScenarioScript.ReadPosition(bubble.GetProperty("position"))) found = true;
                    same &= found;
                }
            }
            _observationsMatch &= same;
        }
    }
}
