using System.Buffers;
using System.Reflection;
using QQT.Core;

// 范围 1 的正式脚本无法区分射线穿透错误。仅在此检查中以私有扫描器构造范围 3 的
// 合成走廊，不增加游戏配置或改变初始属性；检查与业务使用同一个扫描实现。
static partial class Program
{
    private delegate void Trace(Bubble bubble, int range, bool[] covered, bool[] scheduled, int[] pending, ref int count);
    private static FieldInfo Field(string name) => typeof(GameSimulation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Main(string[] args)
    {
        if (args.Length is < 1 or > 2) throw new ArgumentException("需要连锁获胜脚本的绝对路径。");
        CheckRays();
        CheckTermination(args[0]);
        if (args.Length == 2) CheckItemObservationLifetime(args[1]);
        Console.WriteLine("CHAIN_BOUNDARIES Passed=True: penetration, range limit, wall/crate stop, shared old terrain, same-cell deduplication, winner placement cancellation, draw, ended guard and reset.");
    }

    private static void CheckRays()
    {
        using var world = new GameSimulation();
        var terrain = (Cell[])Field("_terrain").GetValue(world)!;
        terrain.AsSpan(0, GameSimulation.CellCount).Clear();
        terrain[new Position(3, 0).Index] = Cell.Crate;
        terrain[new Position(0, 1).Index] = Cell.Wall;
        var bubbles = (Bubble[])Field("_bubbles").GetValue(world)!;
        bubbles[0] = new(1, 1, new(0, 0), 1, 4);
        bubbles[1] = new(2, 2, new(1, 0), 2, 5);
        bubbles[2] = new(3, 3, new(1, 0), 3, 6);
        Field("_bubbleCount").SetValue(world, 3);
        var trace = typeof(GameSimulation).GetMethod("TraceBlast", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Trace>(world);
        var covered = ArrayPool<bool>.Shared.Rent(GameSimulation.CellCount);
        bool[]? scheduled = null;
        int[]? pending = null;
        try
        {
            scheduled = ArrayPool<bool>.Shared.Rent(3);
            pending = ArrayPool<int>.Shared.Rent(3);
            covered.AsSpan(0, GameSimulation.CellCount).Clear(); scheduled.AsSpan(0, 3).Clear();
            scheduled[0] = true; int count = 0;
            trace(bubbles[0], 3, covered, scheduled, pending, ref count);
            Require(covered[new Position(2, 0).Index], "原射线在触发 B1 泡泡后必须继续覆盖 C1。");
            Require(covered[new Position(3, 0).Index] && !covered[new Position(4, 0).Index], "D1 箱子必须覆盖并截断，不能穿透到 E1。");
            Require(!covered[new Position(0, 1).Index], "硬墙不应被覆盖。");
            Require(count == 2 && scheduled[1] && scheduled[2], "同格两颗泡泡必须各入队一次。");
            trace(bubbles[1], 3, covered, scheduled, pending, ref count);
            Require(count == 2 && terrain[new Position(3, 0).Index] == Cell.Crate && !covered[new Position(4, 0).Index], "同批第二条射线仍应被旧箱子截断，重复覆盖不重复入队。");
            int chains = 0; foreach (var e in world.Events) if (e.Kind == EventKind.Chained) chains++;
            Require(chains == 2, "同格连锁事件不能重复。");
            terrain[new Position(3, 0).Index] = Cell.Empty;
            trace(bubbles[0], 3, covered, scheduled, pending, ref count);
            Require(!covered[new Position(4, 0).Index], "触发泡泡不能延长原射程。");
            trace(bubbles[1], 3, covered, scheduled, pending, ref count);
            Require(covered[new Position(4, 0).Index], "箱子清理后的后续批次才可穿过 D1。");
        }
        finally
        {
            if (pending is not null) ArrayPool<int>.Shared.Return(pending);
            if (scheduled is not null) ArrayPool<bool>.Shared.Return(scheduled);
            ArrayPool<bool>.Shared.Return(covered);
        }
    }

    private static void CheckTermination(string path)
    {
        using var script = ScenarioScript.Load(path, 8);
        using var world = new GameSimulation();
        for (int tick = 1; tick <= 3; tick++) world.Advance(script.Round(tick));
        // 合成夹具仅打开 A19，让胜者可以在 t=4 到达安全终点并请求放置。
        // 正式地图下此格是箱子，主脚本因此无法区分存活者的终点放置取消。
        ((Cell[])Field("_terrain").GetValue(world)!)[new Position(0, 18).Index] = Cell.Empty;
        var plans = ArrayPool<PlayerAction>.Shared.Rent(4);
        try
        {
            script.Round(4).CopyTo(plans);
            plans[2] = new(3, Direction.Up, new(0, 18));
            world.Advance(plans.AsSpan(0, 4));
            Require(world.Ended && world.WinnerId == 3 && world.EndTick == 4 && world.Players[2].Position == new Position(0, 18), "应在同批淘汰后获胜。");
            Require(world.BubbleCount == 1 && world.Bubbles[0].Id == 9, "胜者的终点放置必须取消，原遗留泡泡保留。");
            foreach (var e in world.Events) Require(e.Kind != EventKind.Placed, "获胜时不能执行终点放置。");
            bool rejected = false;
            try { world.Advance(plans.AsSpan(0, 4)); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && world.Tick == 4, "结束后的直接推进必须拒绝且保持时间。");
            world.Reset();
            Require(!world.Ended && world.EndTick is null && world.WinnerId is null && world.BubbleCount == 0 && world.CrateCount == 329, "重跑必须清理胜负与泡泡状态。");
            for (int tick = 1; tick <= 3; tick++) world.Advance(script.Round(tick));
            script.Round(4).CopyTo(plans); plans[2] = new(3, Direction.Down, null);
            world.Advance(plans.AsSpan(0, 4));
            Require(world.Ended && world.WinnerId is null && world.EndTick == 4, "第四人也受击时必须平局，不能在全批淘汰前宣布获胜。");
            foreach (var player in world.Players) Require(!player.Alive, "平局夹具应全员死亡。");
        }
        finally { ArrayPool<PlayerAction>.Shared.Return(plans); }
    }
}
