using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class CredentialStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pmw-test-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "credentials.dat");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void RoundTrips()
    {
        var store = new CredentialStore(File_);
        Assert.False(store.Exists);
        Assert.Null(store.Load());

        store.Save(new CredentialStore.Credentials("sess-abc", "tok-xyz"));
        Assert.True(store.Exists);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("sess-abc", loaded!.PoeSessId);
        Assert.Equal("tok-xyz", loaded.PoeToken);
    }

    [Fact]
    public void FileIsNotPlaintext()
    {
        var store = new CredentialStore(File_);
        store.Save(new CredentialStore.Credentials("SUPERSECRETSESSION", "SUPERSECRETTOKEN"));

        var raw = System.IO.File.ReadAllBytes(File_);
        var asText = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("SUPERSECRETSESSION", asText, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSECRETTOKEN", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void ToStringDoesNotLeak()
    {
        // A credential must never be readable from a log line or exception message.
        var creds = new CredentialStore.Credentials("sess-abc", "tok-xyz");
        var text = creds.ToString();
        Assert.DoesNotContain("sess-abc", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-xyz", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sess-abc", $"{creds}", StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFileReturnsNullRatherThanThrowing()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllBytes(File_, "not a dpapi blob"u8.ToArray());
        Assert.Null(new CredentialStore(File_).Load());
    }

    [Fact]
    public void ClearRemovesTheFile()
    {
        var store = new CredentialStore(File_);
        store.Save(new CredentialStore.Credentials("a", "b"));
        store.Clear();
        Assert.False(store.Exists);
        Assert.Null(store.Load());
        store.Clear(); // idempotent
    }

    [Fact]
    public void OverwriteReplacesPreviousValue()
    {
        var store = new CredentialStore(File_);
        store.Save(new CredentialStore.Credentials("first", "t1"));
        store.Save(new CredentialStore.Credentials("second", "t2"));
        Assert.Equal("second", store.Load()!.PoeSessId);
        Assert.False(System.IO.File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void BuildsCookieHeader()
    {
        Assert.Equal("POESESSID=a; POETOKEN=b",
            CredentialStore.ToCookieHeader(new CredentialStore.Credentials("a", "b")));
        Assert.Equal("POESESSID=a",
            CredentialStore.ToCookieHeader(new CredentialStore.Credentials("a", "")));
    }

    [Fact]
    public void DefaultPathIsOutsideTheProgramDirectory()
    {
        // A portable exe on a USB stick must not carry the user's account with it.
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Assert.DoesNotContain(appDir, CredentialStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PoeMarketWatch", CredentialStore.DefaultPath, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteCredentialsAreDetected()
    {
        Assert.False(new CredentialStore.Credentials("", "tok").IsComplete);
        Assert.True(new CredentialStore.Credentials("sess", "").IsComplete);
    }
}
