using System.Windows;

namespace Nuvia.App;

/// <summary>
/// Native, deliberately high-friction confirmation for deleting the one managed private storage group.
/// <para>
/// Deleting the group destroys the remote supergroup and every file stored in it — it is irreversible.
/// This window only collects an explicit confirmation (a checkbox that arms the Delete button, with
/// Cancel as the default so a stray Enter can never delete). It never touches Telegram or the index: the
/// caller performs the deletion only when <see cref="ShowDialog"/> returns <c>true</c>.
/// </para>
/// </summary>
public partial class DeleteGroupWindow : Window
{
    /// <param name="groupTitle">The group's title, shown in the header.</param>
    /// <param name="fileCount">How many indexed files live in the group, for the scope note (0 hides it).</param>
    public DeleteGroupWindow(string groupTitle, int fileCount)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(groupTitle))
            HeaderText.Text = $"Delete “{groupTitle}”?";

        if (fileCount > 0)
        {
            ScopeNote.Text = fileCount == 1
                ? "1 file is stored in this group. It will be permanently deleted from Telegram too."
                : $"{fileCount} files are stored in this group. They will be permanently deleted from Telegram too.";
            ScopeNote.Visibility = Visibility.Visible;
        }

        CancelButton.Focus();
    }

    private void ConfirmCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (DeleteButton is not null)
            DeleteButton.IsEnabled = ConfirmCheck.IsChecked == true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmCheck.IsChecked != true)
            return;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
