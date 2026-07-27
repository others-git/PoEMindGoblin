using System.Windows;
using PoeMarketWatch.Core;

namespace PoeMarketWatch;

/// <summary>
/// Collects POESESSID / POETOKEN.
///
/// Deliberately uses PasswordBox rather than TextBox: these are unscoped full-account
/// credentials, so they should not sit in plain view or land in a screenshot while
/// someone is streaming. Existing values are never loaded back into the UI -- the store
/// is write-then-verify only.
/// </summary>
public partial class CredentialWindow : Window
{
    private readonly CredentialStore _store;

    public CredentialWindow(CredentialStore store)
    {
        InitializeComponent();
        _store = store;
        if (_store.Exists) ErrorText.Text = "A session is already stored. Saving replaces it.";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var sess = SessBox.Password.Trim();
        if (sess.Length == 0)
        {
            ErrorText.Text = "POESESSID is required - the live search socket returns 401 without it.";
            return;
        }

        // A pasted "POESESSID=abc; POETOKEN=def" is a common mistake; accept it gracefully.
        if (sess.Contains('=') || sess.Contains(';'))
        {
            var (s, t) = CredentialStore.SplitCookieHeader(sess);
            if (s is not null)
            {
                _store.Save(new CredentialStore.Credentials(s, t ?? TokenBox.Password.Trim()));
                DialogResult = true;
                return;
            }
        }

        _store.Save(new CredentialStore.Credentials(sess, TokenBox.Password.Trim()));
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _store.Clear();
        SessBox.Clear();
        TokenBox.Clear();
        ErrorText.Text = "Stored session removed.";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
