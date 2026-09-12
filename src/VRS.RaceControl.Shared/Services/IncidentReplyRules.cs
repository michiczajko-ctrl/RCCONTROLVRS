using VRS.RaceControl.Shared.Models;
namespace VRS.RaceControl.Shared.Services;

public static class IncidentReplyRules
{
    public static string? Apply(IncidentReport stored, IncidentDriverReply reply, string driverId, string authorKey, DateTime now)
    {
        if (stored.ReporterId != driverId && (string.IsNullOrWhiteSpace(stored.ReporterAccountKey)
            || !string.Equals(stored.ReporterAccountKey, authorKey, StringComparison.Ordinal)))
            return "Only the reporter can reply to this incident.";
        if (!Guid.TryParseExact(reply.Id, "N", out _) || string.IsNullOrWhiteSpace(reply.Text) || reply.Text.Trim().Length > 600)
            return "Reply requires an identifier and 1–600 characters.";
        stored.DriverReplies ??= new();
        stored.History ??= new();
        if (stored.DriverReplies.Any(item => item.Id == reply.Id)) return null;
        if (stored.DriverReplies.Count >= 100) return "Reply limit reached.";
        stored.DriverReplies.Add(new IncidentDriverReply { Id = reply.Id, Text = reply.Text.Trim(), CreatedAtUtc = now });
        stored.History.Add(new IncidentHistoryEntry { TimestampUtc = now, Actor = stored.ReporterName, Action = "Driver reply received" });
        return null;
    }
}
