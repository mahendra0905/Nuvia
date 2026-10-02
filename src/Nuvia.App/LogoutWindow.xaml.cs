using System.Windows;

namespace Nuvia.App;

/// <summary>
/// Nuvia-themed logout confirmation dialog, replacing the native <c>MessageBox</c>.
/// <para>
/// Like <see cref="DeleteFileWindow"/> this window only collects a yes/no choice; it never touches
/// Telegram, the auth service, or the local session. The caller treats <see cref="ShowDialog"/>
/// returning <c>true</c> as "the user confirmed logout" and performs the actual sign-out. Default
/// focus stays on Cancel so a stray Enter can never sign the user out.
/// </para>
/// </summary>
public partial class LogoutWindow : Window
{
    public LogoutWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        CancelButton.Focus();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
