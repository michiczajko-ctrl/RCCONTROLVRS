using System.Security.Cryptography;
using System.Text.Json;
using VRS.RaceControl.Shared.Models;

namespace VRS.RaceControl.Shared.Services;
public static class TrackDefinitionCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Checksum(TrackDefinition definition) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition, Json)));
    public static TrackDefinitionReference Reference(TrackDefinition definition) => new(Checksum(definition), definition.Simulator,
        definition.TrackId, definition.LayoutId, definition.Revision);
    public static TrackDefinition ForPublication(TrackDefinition definition)
    {
        if (definition.Validate() is { } error) throw new ArgumentException(error, nameof(definition));
        if (!definition.Verified) throw new ArgumentException("Verify measured geometry before publishing it.", nameof(definition));
        if (definition.Spline.Count <= 1000) return definition;
        var geometry = new TrackGeometry(definition);
        var compact = definition with { Spline = Enumerable.Range(0, 1000).Select(i => geometry.AtDistance(i * definition.LengthMeters / 1000)).ToArray() };
        if (compact.Validate() is { } compactError) throw new ArgumentException(compactError, nameof(definition));
        return compact;
    }
}
