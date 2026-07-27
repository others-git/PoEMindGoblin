using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Encrypted-at-rest store for the pathofexile.com session cookies.
///
/// Why cookies and not OAuth, since that is the obvious question:
///
/// GGG's OAuth has exactly twelve scopes, and none of them is trade --
///   account:  profile, leagues, stashes, characters, league_accounts, item_filter
///   service:  leagues, leagues:ladder, pvp_matches, pvp_matches:ladder, psapi, cxapi
/// You cannot request a scope that does not exist. Asked directly about trade access,
/// GGG said only that "the internal APIs currently used by the trade website will remain
/// available without authentication for now". Currency exchange got its own service
/// scope while trade did not, so the omission looks deliberate.
///
/// There is a second wall behind the first: a portable exe is a PUBLIC client (no way to
/// hold a secret), and public clients "cannot use any service:* scopes" at all. So even a
/// hypothetical service:trade would be unusable here; it would have to be account:trade.
///
/// Other PoE apps that do use OAuth are doing OAuth-shaped things -- stash price checks,
/// character import, filter management. Every tool that does LIVE SEARCH uses POESESSID,
/// for this exact reason.
///
/// Measured: search works unauthenticated; the live-search socket and the whisper/travel
/// endpoint both return 401 without cookies.
///
/// So the live features need <c>POESESSID</c> and <c>POETOKEN</c>, which are full-account
/// session credentials -- not scoped, not per-app revocable. Consequences enforced here:
///   * encrypted with DPAPI at CurrentUser scope, so the file is useless on another
///     machine or to another Windows user;
///   * never logged -- <see cref="ToString"/> is overridden on the credential type;
///   * stored outside the program directory so a portable exe on a USB stick does not
///     carry someone's account with it.
/// Treat a leak as "attacker can act as the user on the website" and revoke by logging
/// out of pathofexile.com, which invalidates the session.
/// </summary>
public sealed class CredentialStore
{
    // Tied to this app + purpose so the blob cannot be swapped in from elsewhere.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("PoeMarketWatch/v1/session-cookies");

    private readonly string _path;

    public CredentialStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    /// <summary>%LOCALAPPDATA%\PoeMarketWatch\credentials.dat -- deliberately not next to the exe.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PoeMarketWatch", "credentials.dat");

    public string Path_ => _path;
    public bool Exists => File.Exists(_path);

    public sealed record Credentials(string PoeSessId, string PoeToken)
    {
        /// <summary>
        /// Any other cookies from the same browser session, verbatim.
        ///
        /// Exists because guessing which cookies matter is exactly what went wrong: the
        /// captured browser request carried POESESSID *and* POETOKEN, and Cloudflare may
        /// add cf_clearance. Rather than maintain a list of names GGG never documented,
        /// keep whatever the browser sent and replay it.
        /// </summary>
        public Dictionary<string, string> Extra { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsComplete => !string.IsNullOrWhiteSpace(PoeSessId);

        public IEnumerable<string> CookieNames
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(PoeSessId)) yield return "POESESSID";
                if (!string.IsNullOrWhiteSpace(PoeToken)) yield return "POETOKEN";
                foreach (var k in Extra.Keys) yield return k;
            }
        }

        /// <summary>Guard against a credential landing in a log line or exception message.</summary>
        public override string ToString() =>
            $"Credentials({string.Join(", ", CookieNames.Select(n => n + "=***"))})";
    }

    public void Save(Credentials creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dto(creds.PoeSessId, creds.PoeToken, creds.Extra));
        byte[] blob;
        try
        {
            blob = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            Array.Clear(json); // don't leave plaintext in a pooled buffer
        }

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-move so an interrupted save cannot leave a truncated blob.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Returns null when absent, or when the blob cannot be decrypted by this user.</summary>
    public Credentials? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var blob = File.ReadAllBytes(_path);
            var json = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var dto = JsonSerializer.Deserialize<Dto>(json);
                if (dto is null || string.IsNullOrWhiteSpace(dto.sess)) return null;
                return new Credentials(dto.sess, dto.token ?? "")
                {
                    Extra = dto.extra is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(dto.extra, StringComparer.OrdinalIgnoreCase),
                };
            }
            finally
            {
                Array.Clear(json);
            }
        }
        catch (CryptographicException)
        {
            // Different user or machine, or a corrupt/tampered file. Not fatal --
            // the app should simply ask for the cookies again.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Cookie header value for the trade API, including any extra cookies held.</summary>
    public static string ToCookieHeader(Credentials creds)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(creds.PoeSessId)) parts.Add($"POESESSID={creds.PoeSessId}");
        if (!string.IsNullOrWhiteSpace(creds.PoeToken)) parts.Add($"POETOKEN={creds.PoeToken}");
        foreach (var (k, v) in creds.Extra)
        {
            if (k.Equals("POESESSID", StringComparison.OrdinalIgnoreCase)
                || k.Equals("POETOKEN", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add($"{k}={v}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Parse a whole pasted Cookie header into credentials, keeping every cookie.
    /// This is the reliable path: it captures cf_clearance and anything else GGG or
    /// Cloudflare starts requiring, without this app having to know the names.
    /// </summary>
    public static Credentials? FromCookieHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string? sess = null, token = null;
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            if (key.Equals("POESESSID", StringComparison.OrdinalIgnoreCase)) sess = value;
            else if (key.Equals("POETOKEN", StringComparison.OrdinalIgnoreCase)) token = value;
            else extra[key] = value;
        }
        return sess is null ? null : new Credentials(sess, token ?? "") { Extra = extra };
    }

    /// <summary>
    /// Parse a pasted "POESESSID=abc; POETOKEN=def" header. Pasting the whole cookie
    /// string instead of the bare value is the obvious user mistake, so accept both.
    /// </summary>
    public static (string? Sess, string? Token) SplitCookieHeader(string? header)
    {
        string? sess = null, token = null;
        if (string.IsNullOrWhiteSpace(header)) return (null, null);
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key.Equals("POESESSID", StringComparison.OrdinalIgnoreCase)) sess = value;
            else if (key.Equals("POETOKEN", StringComparison.OrdinalIgnoreCase)) token = value;
        }
        return (sess, token);
    }

    private sealed record Dto(string sess, string? token, Dictionary<string, string>? extra = null);
}
