using QQT.Core;

static partial class Program
{
    // 脚本能验证内容，但不能持有旧观察跨 Advance/Reset；只为这一所有权风险增加必要检查。
    private static void CheckItemObservationLifetime(string path)
    {
        using var script = ScenarioScript.Load(path, 128);
        using var world = new GameSimulation();
        world.Configure(script.Initial!.Value); world.ConfigureItems(script.Items); world.PrepareRound();
        using var before = world.Observe(1);
        Require(before.Cells.Length == 25 && before.Self.Vision == 2 && before.Items.Length == 0, "初始观察必须是隐藏道具的 5×5。");
        world.Advance(script.Round(1));
        Require(world.Players[0].Vision == 2 && world.Players[0].Pending.Vision == 1, "旧轮终点不能提前激活视野。");
        world.PrepareRound(); world.PrepareRound(); // 重复准备不能重复应用增益或清理已有激活事件。
        using var after = world.Observe(1);
        Require(after.Cells.Length == 49 && after.Self.Vision == 3 && after.Items.Length == 1, "新轮应扩大视野并显示新可见道具。");
        Require(before.Cells.Length == 25 && before.Self.Vision == 2 && before.Items.Length == 0 && before.Players.Length == 0 && before.Bubbles.Length == 1, "推进后不能污染旧观察的实体列表。");
        bool oldCrate = false;
        foreach (var cell in before.Cells) if (cell.Position == new Position(11, 10)) oldCrate = cell.Terrain == Cell.Crate;
        Require(oldCrate, "箱子销毁后，旧观察中的地形仍须保持冻结。");
        world.Reset();
        Require(after.Cells.Length == 49 && after.Items.Length == 1 && before.Cells.Length == 25, "Reset 不能使仍在使用的观察失效。");
        Require(world.Players[0].Pending.Total == 0 && world.Players[0].Vision == 2 && world.GeneratedItems.Total == 0 && world.ItemSeed is null, "重跑必须清理增益、生成计数和种子。");
        foreach (var item in world.Items) Require(item == default, "重跑不得残留隐藏、露出或已移除道具。");
        before.Dispose(); before.Dispose();
        bool rejected = false;
        try { _ = before.Cells.Length; } catch (ObjectDisposedException) { rejected = true; }
        Require(rejected, "观察归还后必须拒绝访问池数组。");
        Console.WriteLine("ITEM_OBSERVATION_LIFETIME Passed=True: frozen terrain/entities, next-round activation, idempotent preparation, reset, owned buffers and disposed guard.");
    }
}
