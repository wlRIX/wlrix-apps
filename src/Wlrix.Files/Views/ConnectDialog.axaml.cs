using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Remote;
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.Files.Views;

/// <summary>What the user typed into the connect dialog.</summary>
/// <param name="Location">Where to go. Carries no credentials — a Location never does.</param>
/// <param name="Credentials">How to log in.</param>
/// <param name="Remember">Whether to hand the credential to the store.</param>
public sealed record ConnectRequest(Location Location, ShareCredentials Credentials, bool Remember);

/// <summary>Connecting to a server, and re-authenticating to one.</summary>
/// <remarks>
/// One dialog for both, because they ask for the same things and differ only in which of them
/// are already known. Re-authenticating locks the address so the user cannot answer a password
/// prompt for one server by quietly pointing it at another.
/// </remarks>
public partial class ConnectDialog : Window
{
    public ConnectDialog()
    {
        InitializeComponent();

        Accept.Click += (_, _) => Close(Build());
        Reject.Click += (_, _) => Close(null);

        Scheme.SelectionChanged += (_, _) => ApplyScheme();
        Anonymous.IsCheckedChanged += (_, _) => Login.IsEnabled = Anonymous.IsChecked != true;
    }

    /// <summary>Asks where to connect. Answers null if the user canceled.</summary>
    public static async Task<ConnectRequest?> ShowAsync(Window owner, string title, bool canRemember)
    {
        var dialog = new ConnectDialog { Title = title };
        dialog.Scheme.SelectedIndex = 0;
        dialog.RememberNote.IsVisible = !canRemember;
        dialog.ApplyScheme();
        dialog.Opened += (_, _) => dialog.Host.Focus();
        return await dialog.ShowDialog<ConnectRequest?>(owner);
    }

    /// <summary>
    /// Asks for the credentials of a share we are already pointed at.
    /// </summary>
    /// <remarks>
    /// The address is filled in and disabled rather than absent, so the user can see which
    /// server is asking. A password prompt that does not say what it is for is how people end
    /// up typing one server's password into another.
    /// </remarks>
    public static async Task<ConnectRequest?> ShowForAsync(
        Window owner, string title, ShareRef share, string? why, bool canRemember)
    {
        var dialog = new ConnectDialog { Title = title };
        dialog.Scheme.SelectedIndex = Math.Max(0, Array.IndexOf(Schemes, share.Scheme));
        dialog.Host.Text = share.Host;
        dialog.Share.Text = share.Share;
        dialog.User.Text = share.Username;
        dialog.Address.IsEnabled = false;
        // The folder is not part of what is being authenticated, and an empty disabled field
        // beside a filled one reads as something that failed to load.
        dialog.PathLabel.IsVisible = false;
        dialog.Path.IsVisible = false;
        dialog.RememberNote.IsVisible = !canRemember;

        if (why is { Length: > 0 })
        {
            dialog.Problem.Text = why;
            dialog.Problem.IsVisible = true;
        }

        dialog.ApplyScheme();
        dialog.Opened += (_, _) => dialog.User.Focus();
        return await dialog.ShowDialog<ConnectRequest?>(owner);
    }

    private static readonly string[] Schemes = ["smb", "sftp", "ftp"];

    private string SelectedScheme => Schemes[Math.Max(0, Scheme.SelectedIndex)];

    /// <summary>Shows only the fields the chosen protocol actually has.</summary>
    private void ApplyScheme()
    {
        // SMB is the only one of the three with a share, which is a separate thing from a
        // directory: it is what a connection authenticates against.
        var isSmb = SelectedScheme == "smb";
        ShareLabel.IsVisible = isSmb;
        Share.IsVisible = isSmb;

        // There is no anonymous SFTP. The protocol runs inside an authenticated SSH session,
        // so the box is hidden rather than left there to be ticked and refused.
        var anonymous = SelectedScheme != "sftp";
        Anonymous.IsVisible = anonymous;
        if (!anonymous)
            Anonymous.IsChecked = false;
        Login.IsEnabled = Anonymous.IsChecked != true;
    }

    private ConnectRequest? Build()
    {
        var host = Host.Text?.Trim();
        if (string.IsNullOrEmpty(host))
            return null;

        var scheme = SelectedScheme;
        var path = (Path.Text ?? string.Empty).Trim('/');
        var share = scheme == "smb" ? (Share.Text ?? string.Empty).Trim('/') : string.Empty;
        var full = "/" + string.Join('/', new[] { share, path }.Where(part => part.Length > 0));

        if (!Location.TryParse($"{scheme}://{host}{full}", out var location))
            return null;

        var credentials = Anonymous.IsChecked == true
            ? ShareCredentials.Anonymously
            : new ShareCredentials(User.Text ?? string.Empty, Password.Text ?? string.Empty);

        return new ConnectRequest(location, credentials, Remember.IsChecked == true);
    }
}
