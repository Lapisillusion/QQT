namespace QQT.Server;

public sealed record ScenarioOptions(string ScriptPath, int TickIntervalMilliseconds, int AnimationMilliseconds, int ReceiveBufferBytes, int MessageBufferBytes, int MessagePoolRetained, int MaxScriptTicks)
{
    public static ScenarioOptions Load(IConfiguration config, string? scriptOverride)
    {
        string script = scriptOverride ?? config["Scenario:ScriptPath"] ?? throw new InvalidDataException("缺少脚本路径配置。");
        int tick = Positive(config, "TickIntervalMilliseconds");
        int animation = Positive(config, "AnimationMilliseconds");
        int receive = Positive(config, "ReceiveBufferBytes");
        int message = Positive(config, "MessageBufferBytes");
        int retained = Positive(config, "MessagePoolRetained");
        int maxTicks = Positive(config, "MaxScriptTicks");
        if (receive < 64 || receive > 65536) throw new InvalidDataException("UI 接收缓冲必须在 64~65536 字节之间。");
        return new(Path.GetFullPath(script, AppContext.BaseDirectory), tick, animation, receive, message, retained, maxTicks);
    }
    private static int Positive(IConfiguration config, string key)
    {
        if (!int.TryParse(config[$"Scenario:{key}"], out int value) || value <= 0)
            throw new InvalidDataException($"Scenario:{key} 必须为正整数。");
        return value;
    }
}
