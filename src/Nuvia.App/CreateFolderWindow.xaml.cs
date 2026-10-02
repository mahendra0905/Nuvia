using System.Windows;
using Nuvia.App.Services;

namespace Nuvia.App;

/// <summary>
/// Native dialog for naming a Nuvia folder — reused for both creating a new folder and renaming an
/// existing one (a non-null initial name switches it into rename mode).
/// <para>
/// This window only collects and validates a folder name against <see cref="FolderNames.IsValidName"/>.
/// It never touches Telegram and never touches the index: the caller creates or renames the folder only
/// if <see cref="ShowDialog"/> returns <c>true</c>. Folders are a purely local layer, so nothing here
/// is sent to the account.
/// </para>
/// </summary>
public partial class CreateFolderWindow : Window
{
    /// <summary>The confirmed, trimmed folder name. Valid only when <see cref="ShowDialog"/> returned true.</summary>
    public string FolderName { get; private set; } = string.Empty;

    /// <param name="currentName">
    /// Null (or blank) for "New folder"; the folder's existing name to switch into "Rename folder" mode.
    /// </param>
    public CreateFolderWindow(string? currentName = null)
    {
        InitializeComponent();

        var renameMode = !string.IsNullOrWhiteSpace(currentName);
        if (renameMode)
        {
            Title = "Rename folder";
            HeadingText.Text = "Rename this folder";
            DescriptionText.Text =
                "This renames the folder for your Nuvia library only. If this folder holds files that Nuvia "
                + "uploaded, Nuvia will update their hidden marker so the new name survives a reinstall. "
                + "Nothing visible changes on Telegram.";
            ConfirmButton.Content = "Rename";
            NameBox.Text = currentName!.Trim();
        }

        NameBox.SelectAll();
        NameBox.Focus();
        UpdateConfirmEnabled();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateConfirmEnabled();

    private void UpdateConfirmEnabled()
    {
        var name = NameBox.Text ?? string.Empty;
        var valid = FolderNames.IsValidName(name);

        if (ConfirmButton is not null)
            ConfirmButton.IsEnabled = valid;

        if (ValidationText is null)
            return;

        if (valid || name.Length == 0)
        {
            // Don't nag before anything is typed; the button already guards the empty case.
            ValidationText.Visibility = name.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ValidationText.Text = "Enter a folder name.";
            return;
        }

        ValidationText.Text = name.Length > FolderNames.MaxNameLength
            ? $"Keep the name to {FolderNames.MaxNameLength} characters or fewer."
            : "A folder name can't contain “/” or “\\”, can't be only spaces, and can't be “.” or “..”.";
        ValidationText.Visibility = Visibility.Visible;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text ?? string.Empty;
        if (!FolderNames.IsValidName(name))
        {
            UpdateConfirmEnabled();
            return;
        }

        FolderName = name.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
