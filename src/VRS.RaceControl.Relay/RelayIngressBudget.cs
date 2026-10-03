using VRS.RaceControl.Shared.Models;
using VRS.RaceControl.Shared.Protocol;

public enum RelayIngressDecision { Accept, DisconnectSender, DropData }

/// <summary>Per-sender abuse isolation; shared data overload must not close healthy control sockets.</summary>
public sealed class RelayIngressBudget
{
    private readonly RelaySlidingWindowRateLimiter _maintenance;
    private readonly RelaySlidingWindowRateLimiter _control;
    private readonly RelaySlidingWindowRateLimiter _data;
    private readonly RelaySlidingWindowRateLimiter _sessionData;
    public RelayIngressBudget(int dataPerAccount = 600, int dataPerSession = 5000,
        int controlPerAccount = 120, int maintenancePerAccount = 1200)
    {
        _maintenance = new(maintenancePerAccount, TimeSpan.FromMinutes(1));
        _control = new(controlPerAccount, TimeSpan.FromMinutes(1));
        _data = new(dataPerAccount, TimeSpan.FromMinutes(1));
        _sessionData = new(dataPerSession, TimeSpan.FromMinutes(1));
    }
    public RelayIngressDecision Admit(string account, string session, MessageType type, bool approvedControl)
    {
        if (type is MessageType.Heartbeat or MessageType.OperatorHeartbeat or MessageType.TimeSyncRequest
            or MessageType.Ack or MessageType.AuthorityReceipt or MessageType.AuthorityPlaybackReceipt)
            return _maintenance.TryAcquire(account) ? RelayIngressDecision.Accept : RelayIngressDecision.DisconnectSender;
        if (approvedControl && (AuthorityCommands.IsSemantic(type)
            || type is MessageType.SessionCommand or MessageType.AuthorityTransfer or MessageType.OperatorCommand
                or MessageType.OperatorApproval or MessageType.OperatorPriorityDecision or MessageType.OperatorPriorityTransfer))
            return _control.TryAcquire(account) ? RelayIngressDecision.Accept : RelayIngressDecision.DisconnectSender;
        if (!_data.TryAcquire(account)) return RelayIngressDecision.DisconnectSender;
        return _sessionData.TryAcquire(session) ? RelayIngressDecision.Accept : RelayIngressDecision.DropData;
    }
}
