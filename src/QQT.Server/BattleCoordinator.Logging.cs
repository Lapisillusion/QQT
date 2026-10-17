namespace QQT.Server;

public sealed partial class BattleCoordinator
{
    [LoggerMessage(1100, LogLevel.Information, "MATCH_CREATED GameId={GameId} PlayerCount={PlayerCount} Manual={Manual} Seed={Seed}")]
    private static partial void LogCreated(ILogger logger, string gameId, int playerCount, bool manual, int seed);
    [LoggerMessage(1101, LogLevel.Information, "REGISTERED GameId={GameId} PlayerId={PlayerId}")]
    private static partial void LogRegistered(ILogger logger, string gameId, int playerId);
    [LoggerMessage(1102, LogLevel.Information, "ROUND_OPEN GameId={GameId} RoundId={RoundId} Time={Time}")]
    private static partial void LogRound(ILogger logger, string gameId, int roundId, QQT.Core.RationalTime time);
    [LoggerMessage(1103, LogLevel.Information, "DISCONNECTED GameId={GameId} PlayerId={PlayerId} Phase={Phase} Time={Time}")]
    private static partial void LogDisconnected(ILogger logger, string gameId, int playerId, string phase, QQT.Core.RationalTime time);
    [LoggerMessage(1104, LogLevel.Information, "MATCH_ENDED GameId={GameId} Time={Time} WinnerId={WinnerId}")]
    private static partial void LogEnded(ILogger logger, string gameId, QQT.Core.RationalTime time, int? winnerId);
    [LoggerMessage(1105, LogLevel.Error, "MATCH_ABORTED GameId={GameId} RoundId={RoundId}")]
    private static partial void LogAborted(ILogger logger, Exception exception, string gameId, int roundId);
    [LoggerMessage(1106, LogLevel.Debug, "QUERY GameId={GameId} RoundId={RoundId} PlayerId={PlayerId} RequestId={RequestId} TargetType={TargetType} TargetId={TargetId} Cost={Cost} ActionPoints={ActionPoints} Error={Error}")]
    private static partial void LogQuery(ILogger logger, string gameId, int roundId, int playerId, string requestId, string targetType, int targetId, int cost, int actionPoints, string? error);
    [LoggerMessage(1107, LogLevel.Debug, "PLAN GameId={GameId} RoundId={RoundId} PlayerId={PlayerId} RequestId={RequestId} Locked={Locked} Error={Error}")]
    private static partial void LogPlan(ILogger logger, string gameId, int roundId, int playerId, string requestId, bool locked, string? error);
}
