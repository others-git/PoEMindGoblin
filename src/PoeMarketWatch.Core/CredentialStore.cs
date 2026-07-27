using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Encrypted-at-rest store for the pathofexile.com session cookies.
///
/// The trade API has NO OAuth scope -- GGG publishes scopes for profile, stashes,
/// characters, item filters and the currency exchange, but not trade, and when asked
/// directly they said only that "the internal APIs currently used by the trade website
/// will remain available without authentication for now". Search works unauthenticated;
/// the live-search socket and the whisper/travel endpoint do not.
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
        public bool IsComplete => !string.IsNullOrWhiteSpace(PoeSessId);

        /// <summary>Guard against a credential landing in a log line or exception message.</summary>
        public override string ToString() => "Credentials(POESESSID=***, POETOKEN=***)";
    }

    public void Save(Credentials creds)
    {
        ArgumentNullException.ThrowIfNull(creds);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dto(creds.PoeSessId, creds.PoeToken));
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
                return new Credentials(dto.sess, dto.token ?? "");
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

    /// <summary>Cookie header value for the trade API.</summary>
    public static string ToCookieHeader(Credentials creds)
    {
        var parts = new List<string> { $"POESESSID={creds.PoeSessId}" };
        if (!string.IsNullOrWhiteSpace(creds.PoeToken)) parts.Add($"POETOKEN={creds.PoeToken}");
        return string.Join("; ", parts);
    }

    private sealed record Dto(string sess, string? token);
}
