using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Finds the action token in a trade fetch response without hard-coding its field name.
///
/// Why this is not just <c>listing.whisper_token</c>: GGG does not document the trade API
/// at all, and every maintained community model of the fetch response
/// (klayveR/poe-api-wrappers, Sidekick) still describes <c>listing</c> as
/// method/indexed/stash/whisper/account/price -- none of them has even the <c>fee</c>
/// field that the live API demonstrably returns today. They predate async trade, so
/// copying a field name from them would be guessing.
///
/// What IS known, from a real captured request: the travel button posts
/// <c>{"token": "&lt;JWT&gt;"}</c> to <c>POST /api/trade/whisper</c>, and the JWT carries
/// <c>tok: "hideout"</c> (vs whisper), <c>iss</c> = search id, <c>sub</c> = item hash,
/// and a 300-second TTL. So instead of assuming where it lives, we walk the JSON for a
/// JWT-shaped string, decode its payload, and use <c>tok</c> to classify it. The field
/// name is then *reported* (<see cref="FoundAtPath"/>) so it self-documents on first run
/// and can be pinned later.
/// </summary>
public static class TokenScanner
{
    public sealed record ActionToken(string Token, string Kind, string FoundAtPath)
    {
        /// <summary>"hideout" = travel to the seller's hideout; "whisper" = send the whisper.</summary>
        public bool IsHideout => string.Equals(Kind, "hideout", StringComparison.OrdinalIgnoreCase);

        public override string ToString() => $"ActionToken({Kind} @ {FoundAtPath})";
    }

    /// <summary>Every JWT-ish value in the document, with the JSON path it was found at.</summary>
    public static List<ActionToken> Scan(JsonElement root)
    {
        var found = new List<ActionToken>();
        Walk(root, "$", found);
        return found;
    }

    public static List<ActionToken> Scan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Scan(doc.RootElement.Clone());
    }

    private static void Walk(JsonElement node, string path, List<ActionToken> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                    Walk(prop.Value, $"{path}.{prop.Name}", found);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in node.EnumerateArray())
                    Walk(item, $"{path}[{i++}]", found);
                break;
            case JsonValueKind.String:
                var s = node.GetString();
                if (s is not null && TryReadJwt(s, out var kind))
                    found.Add(new ActionToken(s, kind, path));
                break;
        }
    }

    /// <summary>True if <paramref name="value"/> is a JWT whose payload we can read.</summary>
    public static bool TryReadJwt(string value, out string kind)
    {
        kind = "";
        // Cheap rejects first -- most strings in a listing are prose.
        if (value.Length < 40 || value.Contains(' ')) return false;
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        if (!parts[0].StartsWith("eyJ", StringComparison.Ordinal)) return false;

        var payload = DecodeSegment(parts[1]);
        if (payload is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            kind = doc.RootElement.TryGetProperty("tok", out var tok) && tok.ValueKind == JsonValueKind.String
                ? tok.GetString() ?? ""
                : "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Seconds until the token expires; negative if already expired, null if no exp.</summary>
    public static double? SecondsUntilExpiry(string jwt, DateTimeOffset now)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        var payload = DecodeSegment(parts[1]);
        if (payload is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return null;
            if (!exp.TryGetInt64(out var unix)) return null;
            return (DateTimeOffset.FromUnixTimeSeconds(unix) - now).TotalSeconds;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? DecodeSegment(string segment)
    {
        // base64url, unpadded
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => null!,
        };
        if (padded is null) return null;
        Span<byte> buffer = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, buffer, out var written)
            ? Encoding.UTF8.GetString(buffer[..written])
            : null;
    }
}
