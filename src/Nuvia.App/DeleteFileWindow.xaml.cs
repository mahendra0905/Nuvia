using System.Windows;

namespace Nuvia.App;

/// <summary>
/// Native confirmation dialog for deleting an indexed file.
/// <para>
/// This window only collects a choice; it never touches Telegram or the index itself. The caller
/// reads <see cref="Choice"/> when <see cref="ShowDialog"/> returns <c>true</c> and performs the
/// deletion. The default choice is the reversible one (local-only) so a stray Enter can never destroy
/// a remote file.
/// </para>
/// </summary>
public partial class DeleteFileWindow : Window
{
    /// <summary>What the user confirmed. Valid only when <see cref="ShowDialog"/> returned true.</summary>
    public DeleteScope Choice { get; private set; } = DeleteScope.LocalOnly;

    /// <param name="fileName">Display name of the file, shown in the header.</param>
    /// <param name="remoteDeleteBlockedReason">
    /// When non-null, the remote message cannot be deleted (e.g. its group is unreachable): the
    /// "delete everywhere" option is disabled and this reason is shown.
    /// </param>
    public DeleteFileWindow(string fileName, string? remoteDeleteBlockedReason = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(fileName))
            HeaderText.Text = $"Delete “{fileName}”";

        ApplyRemoteBlock(remoteDeleteBlockedReason);
        DeleteButton.Focus();
    }

    /// <summary>
    /// Batch variant: one confirmation covering <paramref name="fileCount"/> selected files. The scope the
    /// user picks (local-only or everywhere) is applied to all of them by the caller. The wording switches
    /// to the plural, but the default choice is still the reversible local-only removal.
    /// </summary>
    /// <param name="fileCount">How many files the confirmation covers (always more than one here).</param>
    /// <param name="remoteDeleteBlockedReason">
    /// When non-null, the remote messages cannot be deleted (e.g. not connected): the "delete everywhere"
    /// option is disabled and this reason is shown.
    /// </param>
    public DeleteFileWindow(int fileCount, string? remoteDeleteBlockedReason = null)
    {
        InitializeComponent();

        Title = "Delete files";
        HeaderText.Text = $"Delete {fileCount} files";
        IntroText.Text =
            "Choose what to delete for all selected files. Removing them from Nuvia only clears the local "
            + "index entries; your files stay in Telegram. Deleting everywhere also removes the messages "
            + "from Telegram and cannot be undone.";
        LocalOnlyDesc.Text =
            "Drops the entries from Nuvia's local list. The files remain in your Telegram storage and can "
            + "be indexed again later.";
        EverywhereDesc.Text =
            "Permanently deletes the messages from Telegram and removes the local entries. This cannot be undone.";

        ApplyRemoteBlock(remoteDeleteBlockedReason);
        DeleteButton.Focus();
    }

    private void ApplyRemoteBlock(string? remoteDeleteBlockedReason)
    {
        if (!string.IsNullOrWhiteSpace(remoteDeleteBlockedReason))
        {
            EverywhereRadio.IsEnabled = false;
            LocalOnlyRadio.IsChecked = true;
            RemoteNote.Text = remoteDeleteBlockedReason;
            RemoteNote.Visibility = Visibility.Visible;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = EverywhereRadio.IsChecked == true ? DeleteScope.Everywhere : DeleteScope.LocalOnly;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

/// <summary>Which deletion the user confirmed in <see cref="DeleteFileWindow"/>.</summary>
public enum DeleteScope
{
    /// <summary>Remove only Nuvia's local index row; the Telegram message is left untouched.</summary>
    LocalOnly,

    /// <summary>Delete the remote Telegram message and then remove the local index row. Irreversible.</summary>
    Everywhere,
}
