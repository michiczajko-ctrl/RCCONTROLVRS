using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;

/// <summary>All operations run under the main host's command gate; never trust a payload actor id.</summary>
public sealed class OperatorPriority
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, OperatorSeat> _seats = new();
    private readonly HashSet<Guid> _commands = new();
    private readonly Queue<Guid> _order = new();
    public string MainId { get; }
    public string? ControllerId { get; private set; }
    public long Generation { get; private set; } = 1;
    public long Revision { get; private set; } = 1;
    public string? PendingControllerId { get; private set; }
    public OperatorPriority(string mainId, string name, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(mainId)) throw new ArgumentException("Main HOST id is required.", nameof(mainId));
        MainId = ControllerId = mainId;
        _seats[mainId] = new(mainId, name, OperatorRole.MainHost,
            OperatorPermissions.Observe | OperatorPermissions.Incidents | OperatorPermissions.Control, now, true);
    }
    public OperatorPriorityState Snapshot()
    {
        lock (_gate)
            return new(MainId, ControllerId, Generation, Revision, PendingControllerId,
                _seats.Values.OrderBy(seat => seat.Id, StringComparer.Ordinal).ToArray());
    }
    public bool Approve(string actor, string id, string name, OperatorPermissions permissions, DateTime now)
        => Approve(actor, id, name, RoleFor(permissions), permissions, now);
    public bool Approve(string actor, string id, string name, OperatorRole role,
        OperatorPermissions permissions, DateTime now)
    {
        lock (_gate)
        {
            if (!IsMainActorLocked(actor) || id == MainId || string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(name) || role == OperatorRole.MainHost) return false;
            permissions = NormalizePermissions(role, permissions);
            _seats[id] = new(id, name.Trim(), role, permissions, now, true);
            if (ControllerId == id && !permissions.HasFlag(OperatorPermissions.Control))
                SetControllerLocked(MainId);
            if (PendingControllerId == id && !permissions.HasFlag(OperatorPermissions.Control))
                PendingControllerId = null;
            Revision++;
            return true;
        }
    }
    public bool Heartbeat(string id, DateTime now)
    {
        lock (_gate)
        {
            if (!_seats.TryGetValue(id, out var seat)) return false;
            _seats[id] = seat with { LastHeartbeatUtc = now, IsConnected = true };
            if (id == MainId && ControllerId == null)
            {
                SetControllerLocked(MainId);
                Revision++;
            }
            return true;
        }
    }
    public bool Transfer(string actor, string target, DateTime now)
    {
        lock (_gate)
        {
            if (!IsMainActorLocked(actor) || !CanControlLocked(target, now)) return false;
            PendingControllerId = null;
            SetControllerLocked(target);
            Revision++;
            return true;
        }
    }
    public bool RequestControl(string actor, DateTime now)
    {
        lock (_gate)
        {
            if (actor == MainId || !CanControlLocked(actor, now)) return false;
            PendingControllerId = actor;
            Revision++;
            return true;
        }
    }
    public bool DecideControlRequest(string actor, string target, bool approve, DateTime now)
    {
        lock (_gate)
        {
            if (!IsMainActorLocked(actor) || PendingControllerId != target) return false;
            PendingControllerId = null;
            if (approve)
            {
                if (!CanControlLocked(target, now)) return false;
                SetControllerLocked(target);
            }
            Revision++;
            return true;
        }
    }
    public bool Revoke(string actor, string target)
    {
        lock (_gate)
        {
            if (!IsMainActorLocked(actor) || target == MainId || !_seats.Remove(target)) return false;
            if (PendingControllerId == target) PendingControllerId = null;
            if (ControllerId == target) SetControllerLocked(MainId);
            Revision++;
            return true;
        }
    }
    public bool Disconnect(string id)
    {
        lock (_gate)
        {
            if (!_seats.TryGetValue(id, out var seat) || !seat.IsConnected) return false;
            _seats[id] = seat with { IsConnected = false };
            if (PendingControllerId == id) PendingControllerId = null;
            if (id == MainId) SetControllerLocked(null);
            else if (ControllerId == id)
                SetControllerLocked(_seats[MainId].IsConnected ? MainId : null);
            Revision++;
            return true;
        }
    }
    public bool Expire(DateTime now)
    {
        lock (_gate) return ExpireLocked(now);
    }
    public bool AcceptControl(string authenticatedActor, long generation, Guid commandId, DateTime now)
    {
        lock (_gate)
        {
            ExpireLocked(now);
            if (ControllerId == null || authenticatedActor != ControllerId || generation != Generation
                || commandId == Guid.Empty || !_commands.Add(commandId)) return false;
            _order.Enqueue(commandId);
            if (_order.Count > 4096) _commands.Remove(_order.Dequeue());
            return true;
        }
    }
    public bool HasPermission(string authenticatedActor, OperatorPermissions permission, DateTime now)
    {
        lock (_gate)
        {
            ExpireLocked(now);
            return _seats.TryGetValue(authenticatedActor, out var seat) && seat.IsConnected
                && now - seat.LastHeartbeatUtc < HeartbeatTimeout && seat.Permissions.HasFlag(permission);
        }
    }

    private bool ExpireLocked(DateTime now)
    {
        if (ControllerId == null || ControllerId == MainId) return false;
        if (_seats.TryGetValue(ControllerId, out var current) && current.IsConnected
            && now - current.LastHeartbeatUtc < HeartbeatTimeout) return false;
        SetControllerLocked(_seats[MainId].IsConnected ? MainId : null);
        Revision++;
        return true;
    }
    private bool IsMainActorLocked(string actor) => actor == MainId && _seats[MainId].IsConnected;
    private bool CanControlLocked(string target, DateTime now) => _seats.TryGetValue(target, out var seat)
        && seat.IsConnected && seat.Permissions.HasFlag(OperatorPermissions.Control)
        && (target == MainId || now - seat.LastHeartbeatUtc < HeartbeatTimeout);
    private void SetControllerLocked(string? id)
    {
        if (ControllerId == id) return;
        ControllerId = id;
        Generation++;
    }
    private static OperatorRole RoleFor(OperatorPermissions permissions)
        => permissions.HasFlag(OperatorPermissions.Control) ? OperatorRole.Operator
            : permissions.HasFlag(OperatorPermissions.Incidents) ? OperatorRole.Steward : OperatorRole.Observer;
    private static OperatorPermissions NormalizePermissions(OperatorRole role, OperatorPermissions requested) => role switch
    {
        OperatorRole.Observer => OperatorPermissions.Observe,
        OperatorRole.Steward => OperatorPermissions.Observe | OperatorPermissions.Incidents,
        OperatorRole.Operator => requested | OperatorPermissions.Observe,
        _ => OperatorPermissions.None
    };
}
