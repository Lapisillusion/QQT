namespace QQT.Server;

public sealed record ScenarioOptions(string ScriptPath, int TickIntervalMilliseconds, int AnimationMilliseconds, int ReceiveBufferBytes, int MessageBufferBytes, int MessagePoolRetained, int MaxScriptTicks)
{
    public int OutgoingQueueCapacity { get; init; }
    public int InitialEventCapacity { get; init; }
}
