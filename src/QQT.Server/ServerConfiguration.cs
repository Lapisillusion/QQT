using System.Text.Json;
using QQT.Core;

namespace QQT.Server;

/// <summary>唯一配置文件入口。每次读取拥有独立 JSON 文档；正在运行的对局保留自己的快照。</summary>
public sealed class ServerConfiguration(string path, string? scriptOverride = null)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public ServerSettings Read()
    {
        try { return ServerSettings.Read(Path, scriptOverride); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new InvalidDataException($"无法读取配置 {Path}：{exception.Message}", exception); }
    }
}

public sealed class ServerSettings : IDisposable
{
    private readonly JsonDocument _document;
    public string FilePath { get; }
    public string Urls { get; }
    public string RecordDirectory { get; }
    public ScenarioOptions Scenario { get; }
    public PlayerDefaults Player { get; }
    public int PlayerCount { get; }
    public bool ManualTickAdvance { get; }
    public int? Seed { get; }
    public int MaxTicks { get; }
    public int ActionPoints { get; }
    public int BubbleCost { get; }
    public int VisiblePlayerCost { get; }
    public int OutsidePlayerCost { get; }
    public int CatchUpThreshold { get; }
    public double PlaybackSpeed { get; }
    public JsonElement? Items => _document.RootElement.TryGetProperty("Items", out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    public JsonElement? Initial => _document.RootElement.TryGetProperty("Initial", out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private ServerSettings(string path, JsonDocument document, string? scriptOverride)
    {
        FilePath = path; _document = document;
        var root = document.RootElement;
        string directory = System.IO.Path.GetDirectoryName(path)!;
        Urls = Required(root, "Urls").GetString() is { Length: > 0 } urls ? urls : throw new InvalidDataException("Urls 不能为空。");
        var scenario = Required(root, "Scenario");
        string script = scriptOverride ?? System.IO.Path.GetFullPath(Required(scenario, "ScriptPath").GetString()!, directory);
        Scenario = new(script, Positive(scenario, "TickIntervalMilliseconds"), Positive(scenario, "AnimationMilliseconds"),
            Positive(scenario, "ReceiveBufferBytes"), Positive(scenario, "MessageBufferBytes"), Positive(scenario, "MessagePoolRetained"), Positive(scenario, "MaxScriptTicks"))
        { OutgoingQueueCapacity = Positive(scenario, "OutgoingQueueCapacity"), InitialEventCapacity = Positive(scenario, "InitialEventCapacity") };
        if (Scenario.ReceiveBufferBytes is < 64 or > 65536) throw new InvalidDataException("ReceiveBufferBytes 必须在 64~65536 之间。");
        var match = Required(root, "Match");
        PlayerCount = Positive(match, "PlayerCount");
        if (PlayerCount is < 2 or > 4) throw new InvalidDataException("PlayerCount 必须为 2、3 或 4。");
        ManualTickAdvance = Required(match, "ManualTickAdvance").GetBoolean();
        MaxTicks = Positive(match, "MaxTicks");
        Seed = match.TryGetProperty("Seed", out var seed) && seed.ValueKind != JsonValueKind.Null ? seed.GetInt32() : null;
        var player = Required(root, "Player");
        Player = new(Positive(player, "Speed"), Positive(player, "BubbleCapacity"), Positive(player, "BlastRange"),
            Required(player, "VisionRadius").GetInt32(), RationalTime.Read(Required(player, "BubbleLifetime")));
        Player.Validate();
        var query = Required(root, "Queries");
        ActionPoints = Nonnegative(query, "ActionPoints"); BubbleCost = Nonnegative(query, "BubbleCost");
        VisiblePlayerCost = Nonnegative(query, "VisiblePlayerCost"); OutsidePlayerCost = Nonnegative(query, "OutsidePlayerCost");
        var ui = Required(root, "Ui");
        CatchUpThreshold = Positive(ui, "CatchUpThreshold"); PlaybackSpeed = Required(ui, "PlaybackSpeed").GetDouble();
        if (!double.IsFinite(PlaybackSpeed) || PlaybackSpeed <= 0) throw new InvalidDataException("PlaybackSpeed 必须为有限正数。");
        string recordDirectory = Required(Required(root, "Battle"), "RecordDirectory").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(recordDirectory)) throw new InvalidDataException("RecordDirectory 不能为空。");
        RecordDirectory = System.IO.Path.GetFullPath(recordDirectory, directory);
        // 在临时世界校验地图、道具和属性，错误文件不能在开局后才被发现。
        using var candidate = new GameSimulation(Player, Scenario.InitialEventCapacity, PlayerCount, MaxTicks);
        ConfigureWorld(candidate, PlayerCount, Seed, Initial, Items);
    }

    public static ServerSettings Read(string path, string? scriptOverride = null)
    {
        using var stream = File.OpenRead(path);
        var document = JsonDocument.Parse(stream);
        try { return new(path, document, scriptOverride); }
        catch { document.Dispose(); throw; }
    }

    public void ConfigureWorld(GameSimulation world, int count, int? seed, JsonElement? initial, JsonElement? items)
    {
        world.Reset(count, seed, Player); world.TickLimit = MaxTicks;
        if (initial is { ValueKind: not JsonValueKind.Null } environment) world.Configure(environment);
        world.ConfigureItems(items is { ValueKind: JsonValueKind.Null } ? null : items);
    }

    /// <summary>对 AI 公开的规则参数；不包含道具分配或地图隐藏状态。</summary>
    public void WriteRules(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("rules"); writer.WriteNumber("maxTicks", MaxTicks); writer.WriteNumber("actionPoints", ActionPoints);
        writer.WriteNumber("bubbleCost", BubbleCost); writer.WriteNumber("visiblePlayerCost", VisiblePlayerCost);
        writer.WriteNumber("outsidePlayerCost", OutsidePlayerCost); writer.WriteEndObject();
    }
    public void WriteSnapshot(Utf8JsonWriter writer) { writer.WritePropertyName("settings"); _document.RootElement.WriteTo(writer); }
    private static JsonElement Required(JsonElement value, string key) => value.TryGetProperty(key, out var result)
        ? result : throw new InvalidDataException($"缺少配置项 {key}。");
    private static int Positive(JsonElement value, string key) => Integer(value, key, false);
    private static int Nonnegative(JsonElement value, string key) => Integer(value, key, true);
    private static int Integer(JsonElement value, string key, bool zero)
    {
        int result = Required(value, key).GetInt32();
        return result >= (zero ? 0 : 1) ? result : throw new InvalidDataException($"配置项 {key} 必须为{(zero ? "非负" : "正")}整数。");
    }
    public void Dispose() => _document.Dispose();
}
