using System.Text.Json;

namespace VRS.RaceControl.Shared.Models;

public sealed record CloudDocument(string EntityType, string EntityId, JsonElement Payload, long Revision, bool Deleted = false);
public sealed record CloudSnapshot(CloudDocument[] Documents, LeagueConfigurationSnapshot? Configuration, long Cursor);
public sealed record CloudPushResult(bool Accepted, long Revision, string? Error = null);

