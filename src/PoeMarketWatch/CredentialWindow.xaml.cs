using System.Windows;
using PoeMarketWatch.Core;

namespace PoeMarketWatch;

/// <summary>
/// Collects the session cookies.
///
/// The primary path is "paste the whole Cookie header", because guessing which cookies
/// the API needs is exactly what fails: POESESSID alone gets a 401, the browser also
/// sends POETOKEN, and Cloudflare may add cf_clearance. Keeping the entire header means
/// this app never has to know the list.
///
/// PasswordBox rather than TextBox throughout: these are unscoped full-account
/// credentials and should not sit in plain view or land in a screenshot. Stored values
/// are never read back into the UI.
/// </summary>
public partial class CredentialWindow : Window
{
    private readonly CredentialStore _store;
    private readonly AppSettings _settings;

    public CredentialWindow(CredentialStore store, AppSettings settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        UaBox.Text = settings.UserAgent;

        if (_store.Load() is { } existing)
            ErrorText.Text = $"Stored now: {string.Join(", ", existing.CookieNames)}. Saving replaces it.";
        else if (_store.Exists)
            ErrorText.Text = "A session is stored but could not be decrypted. Saving replaces it.";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var header = HeaderBox.Password.Trim();
        CredentialStore.Credentials? creds = null;

        if (header.Length > 0)
        {
            creds = CredentialStore.FromCookieHeader(header);
            if (creds is null)
            {
                ErrorText.Text = "That header has no POESESSID in it. Copy the whole 'cookie' "
                               + "request header, not the response 'set-cookie'.";
                return;
            }
        }
        else
        {
            var sess = SessBox.Password.Trim();
            if (sess.Length == 0)
            {
                ErrorText.Text = "Paste the Cookie header above, or enter POESESSID.";
                return;
            }
            // Tolerate a full header pasted into the single-value box too.
            creds = sess.Contains('=') || sess.Contains(';')
                ? CredentialStore.FromCookieHeader(sess)
                : null;
            creds ??= new CredentialStore.Credentials(sess, TokenBox.Password.Trim());

            // A POETOKEN typed separately should win over one parsed out of the box.
            var token = TokenBox.Password.Trim();
            if (token.Length > 0 && creds.PoeToken != token)
                creds = creds with { PoeToken = token };
        }

        _store.Save(creds);

        var ua = UaBox.Text.Trim();
        if (ua.Length > 0 && ua != _settings.UserAgent)
        {
            _settings.UserAgent = ua;
            _settings.Save();
        }

        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _store.Clear();
        HeaderBox.Clear();
        SessBox.Clear();
        TokenBox.Clear();
        ErrorText.Text = "Stored session removed.";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
