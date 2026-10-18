using System.Buffers;
using System.Text.Json;
using System.Text;
using QQT.ClientAI;
using QQT.Core;

static string Config(string self, string bubbles = "[]", string crates = "[]", int enemyRange = 1) => $$"""
{"map":"empty","players":[{"playerId":1,"vision":20,{{self}}},{"playerId":2,"position":{"x":20,"y":20},"range":{{enemyRange}}}],"bubbles":{{bubbles}},"crates":{{crates}}}
""";
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

// 测试适配器只把真实可见观察及逐颗合法详情写入客户端；完整世界只用于独立的权威预期对照。
static WorldMemory Observe(GameSimulation world)
{
    world.PrepareRound(); using var observed = world.Observe(1); using var buffer = new MessageBuffer(); var w = buffer.Writer;
    var self = observed.Self; w.WriteStartObject(); w.WritePropertyName("time"); world.Time.Write(w);
    w.WriteStartObject("self"); w.WriteNumber("playerId", 1); Position(w, "position", self.Position);
    int owned = 0; foreach (var b in world.Bubbles) if (b.OwnerId == 1) owned++;
    w.WriteNumber("speed", self.Speed); w.WriteNumber("bubbleCapacity", self.Capacity); w.WriteNumber("activeBubbleCount", owned);
    w.WriteNumber("blastRange", self.Range); w.WriteNumber("visionRadius", self.Vision); w.WriteNumber("actionPoints", 10);
    w.WritePropertyName("bubbleLifetime"); self.Lifetime.Write(w); w.WriteEndObject();
    w.WriteStartArray("participantIds"); w.WriteEndArray(); w.WriteStartObject("visible");
    w.WriteStartArray("cells"); foreach (var c in observed.Cells)
    {
        w.WriteStartObject(); Position(w, "position", c.Position); w.WriteString("terrain", c.Terrain.ToString().ToLowerInvariant()); w.WriteEndObject();
    }
    w.WriteEndArray(); w.WriteStartArray("players"); foreach (var p in observed.Players)
    { w.WriteStartObject(); w.WriteNumber("playerId", p.Id); Position(w, "position", p.Position); w.WriteEndObject(); }
    w.WriteEndArray(); w.WriteStartArray("bubbles"); foreach (var b in observed.Bubbles)
    { w.WriteStartObject(); w.WriteNumber("queryId", b.QueryId); Position(w, "position", b.Position); w.WriteEndObject(); }
    w.WriteEndArray(); w.WriteStartArray("items"); foreach (var item in observed.Items)
    { w.WriteStartObject(); Position(w, "position", item.Position); w.WriteString("type", ItemNames.Name(item.Type)); w.WriteEndObject(); }
    w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject(); w.Flush();
    var memory = new WorldMemory();
    try
    {
        using (var doc = JsonDocument.Parse(buffer.Memory)) memory.Observe(world.Tick + 1, doc.RootElement);
        for (int i = 0; i < world.Bubbles.Length; i++)
        {
            var bubble = world.Bubbles[i]; buffer.Reset(); w = buffer.Writer; w.WriteStartObject();
            w.WriteBoolean("success", true); w.WriteNumber("actionPoints", 9); w.WriteString("targetType", "bubble"); w.WriteNumber("targetId", i + 1);
            w.WriteStartObject("data"); w.WriteNumber("ownerId", bubble.OwnerId); w.WriteNumber("blastRange", world.Players[bubble.OwnerId - 1].Range);
            w.WritePropertyName("dueAt"); bubble.DueAt.Write(w); w.WriteEndObject(); w.WriteEndObject(); w.Flush();
            using var doc = JsonDocument.Parse(buffer.Memory); memory.QueryResult(doc.RootElement);
        }
        return memory;
    }
    catch { memory.Dispose(); throw; }
}
static void Position(Utf8JsonWriter w, string name, Position p)
{ w.WriteStartObject(name); w.WriteNumber("x", p.X); w.WriteNumber("y", p.Y); w.WriteEndObject(); }
static void Advance(GameSimulation world, Plan plan, Plan? enemy = null)
{
    var actions = ArrayPool<PlayerAction>.Shared.Rent(2); var placements = ArrayPool<Position>.Shared.Rent(42);
    try
    {
        int n = 0; for (int i = 0; i <= 20; i++) if ((plan.Placements & (1U << i)) != 0) placements[n++] = plan.At(world.Players[0].Position, i);
        actions[0] = new(1, plan.Direction, null) { Placements = placements.AsMemory(0, n) };
        int second = 0; var opponent = enemy ?? new Plan(Direction.Wait, 0);
        for (int i = 0; i <= 20; i++) if ((opponent.Placements & (1U << i)) != 0) placements[21 + second++] = opponent.At(world.Players[1].Position, i);
        actions[1] = new(2, opponent.Direction, null) { Placements = placements.AsMemory(21, second) };
        world.Advance(actions.AsSpan(0, 2));
    }
    finally { ArrayPool<PlayerAction>.Shared.Return(actions, true); ArrayPool<Position>.Shared.Return(placements); }
}
static void Run(string name, string config, Plan plan, bool alive, Position endpoint, RationalTime? death = null)
{
    using var doc = JsonDocument.Parse(config); using var world = new GameSimulation(); world.Reset(2); world.Configure(doc.RootElement); world.ConfigureItems(null);
    using var memory = Observe(world); using var forecast = new Forecast(); forecast.Build(memory, plan, 5);
    Advance(world, plan); var predicted = forecast.RootMotion; var actual = world.Players[0];
    Require(actual.Alive == alive && actual.Position == endpoint && (death is null || actual.DeathTick == death), name + ": 权威输入未达到独立预期。");
    Require(predicted.Alive == actual.Alive && predicted.Position == actual.Position && (alive || predicted.DeathAt == actual.DeathTick), name + ": 客户端预测与 Core 不同。");
    Console.WriteLine("PASS " + name);
}

Run("crossing-blocked-before-same-time-explosion", Config("\"position\":{\"x\":0,\"y\":0}", """[{"ownerId":2,"position":{"x":1,"y":0},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}}]"""), new(Direction.Right, 0), false, new(0, 0), new(1, 2));
Run("earlier-explosion-opens-later-path", Config("\"position\":{\"x\":0,\"y\":0},\"speed\":2", """[{"ownerId":2,"position":{"x":2,"y":0},"placedAt":{"numerator":-14,"denominator":5},"dueAt":{"numerator":1,"denominator":5}}]"""), new(Direction.Right, 0), true, new(2, 0));
Run("crossing-out-at-explosion-is-safe", Config("\"position\":{\"x\":0,\"y\":0}", """[{"ownerId":2,"position":{"x":0,"y":1},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}}]"""), new(Direction.Right, 0), true, new(1, 0));
Run("crossing-in-at-explosion-is-hit", Config("\"position\":{\"x\":0,\"y\":0}", """[{"ownerId":2,"position":{"x":1,"y":1},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}}]"""), new(Direction.Right, 0), false, new(1, 0), new(1, 2));
Run("return-to-last-center-and-chain", Config("\"position\":{\"x\":0,\"y\":0},\"speed\":3", """[{"ownerId":2,"position":{"x":1,"y":1},"placedAt":{"numerator":-9,"denominator":4},"dueAt":{"numerator":3,"denominator":4}}]""", """[{"x":2,"y":0}]"""), new(Direction.Right, 2), false, new(1, 0), new(3, 4));
Run("simultaneous-rays-share-old-crates", Config("\"position\":{\"x\":4,\"y\":2}", """[{"ownerId":2,"position":{"x":1,"y":2},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}},{"ownerId":2,"position":{"x":2,"y":2},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}}]""", """[{"x":3,"y":2}]""", 4), new(Direction.Wait, 0), true, new(4, 2));
Run("chain-does-not-stop-original-ray", Config("\"position\":{\"x\":4,\"y\":2}", """[{"ownerId":2,"position":{"x":1,"y":2},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}},{"ownerId":1,"position":{"x":2,"y":2},"placedAt":-1,"dueAt":2}]""", enemyRange: 4), new(Direction.Wait, 0), false, new(4, 2), new(1, 2));

using (var doc = JsonDocument.Parse(Config("\"position\":{\"x\":0,\"y\":0},\"speed\":3")))
using (var world = new GameSimulation())
{
    world.Reset(2); world.Configure(doc.RootElement); world.ConfigureItems(null); using var memory = Observe(world); using var forecast = new Forecast();
    forecast.Build(memory, new(Direction.Right, 6), 5); Advance(world, new(Direction.Right, 6));
    Require(forecast.RootPlaced == 2 && world.Bubbles.Length == 2 && world.Bubbles[0].DueAt == new RationalTime(10, 3) && world.Bubbles[1].DueAt == new RationalTime(11, 3), "独立沿途时刻。");
    int roots = 0; foreach (var b in forecast.Bubbles) if (b.Root) { Require(b.Due == world.Bubbles[roots].DueAt, "预测沿途到期。"); roots++; }
    var escape = forecast.Move(new(3, 0), 3, Direction.Down, 1); Advance(world, new(Direction.Down, 0));
    Require(escape.Alive && world.Players[0].Alive && escape.Position == world.Players[0].Position, "未来移动对照。");
    Console.WriteLine("PASS independent-placement-fractions-and-future-motion");
}

using (var doc = JsonDocument.Parse(Config("\"position\":{\"x\":0,\"y\":0}", """[{"ownerId":2,"position":{"x":2,"y":0},"placedAt":{"numerator":-11,"denominator":4},"dueAt":{"numerator":1,"denominator":4}},{"ownerId":1,"position":{"x":1,"y":2},"placedAt":{"numerator":-1,"denominator":2},"dueAt":{"numerator":5,"denominator":2}}]""", """[{"x":1,"y":0}]""")))
using (var items = JsonDocument.Parse("""{"mode":"fixed","placements":[{"position":{"x":1,"y":0},"type":"range"}]}"""))
using (var world = new GameSimulation())
{
    world.Reset(2); world.Configure(doc.RootElement); world.ConfigureItems(items.RootElement); Advance(world, new(Direction.Wait, 0));
    using var memory = Observe(world); using var forecast = new Forecast(); forecast.Build(memory, new(Direction.Right, 0), 5);
    Advance(world, new(Direction.Right, 0)); Require(forecast.RootMotion.RangeGain == 1, "根行动拾取。");
    var future = forecast.Move(new(1, 0), 1, Direction.Wait, 2); Advance(world, new(Direction.Wait, 0));
    Require(!future.Alive && !world.Players[0].Alive && world.Players[0].DeathTick == new RationalTime(5, 2), "增益扩张旧泡泡。");
    Console.WriteLine("PASS next-round-range-expands-existing-bubble");
}
var receive = ArrayPool<byte>.Shared.Rent(256);
try
{
    int length = Encoding.UTF8.GetBytes("{\"type\":\"register.result\",\"payload\":{\"playerId\":4}}", receive);
    using var queued = ClientSession.ParseOwned(receive.AsSpan(0, length)); receive.AsSpan(0, length).Fill((byte)'x');
    Require(queued.RootElement.GetProperty("type").GetString() == "register.result" && queued.RootElement.GetProperty("payload").GetProperty("playerId").GetInt32() == 4, "接收缓冲复用破坏排队文档。");
    Console.WriteLine("PASS queued-message-owns-bytes-after-receive-reuse");
}
finally { ArrayPool<byte>.Shared.Return(receive); }
using (var doc = JsonDocument.Parse("""{"map":"empty","players":[{"playerId":1,"position":{"x":0,"y":0},"speed":2,"vision":20},{"playerId":2,"position":{"x":2,"y":1},"speed":2,"capacity":1}],"bubbles":[{"ownerId":2,"position":{"x":20,"y":18},"placedAt":{"numerator":-5,"denominator":2},"dueAt":{"numerator":1,"denominator":2}}]}"""))
using (var world = new GameSimulation())
{
    world.Reset(2); world.Configure(doc.RootElement); world.ConfigureItems(null); using var memory = Observe(world); using var forecast = new Forecast();
    memory.Enemies[2] = new EnemyState { Alive = true, Located = true, Position = new(2, 1), Speed = 2, Capacity = 1, Active = 1, Range = 1 };
    forecast.Build(memory, new(Direction.Right, 0), 5, 2, new(2, 0), new RationalTime(1, 2));
    Advance(world, new(Direction.Right, 0), new(Direction.Up, 2));
    Require(world.Players[0].Alive && world.Players[0].Position == new Position(1, 0) && forecast.RootMotion.Position == world.Players[0].Position, "轮内释放容量后的沿途放泡应阻挡进入。");
    Require(world.Bubbles.Length == 1 && world.Bubbles[0].DueAt == new RationalTime(7, 2), "反击泡泡精确放置时刻。");
    Console.WriteLine("PASS mid-round-capacity-release-and-opponent-placement");
}
Console.WriteLine("11 meaningful checks passed");
