using System.Windows;
using Nuvia.App.Services;

namespace Nuvia.App;

/// <summary>
/// Native confirmation dialog for renaming a file's <b>local display name</b>.
/// <para>
/// This window only collects and validates a name. It never touches Telegram and never touches the
/// index: the caller performs the local-only rename (updating a single column) if
/// <see cref="ShowDialog"/> returns <c>true</c>. The file's original filename and every remote
/// identifier are outside this dialog's reach by construction.
/// </para>
/// </summary>
public partial class RenameWindow : Window
{
    /// <summary>The confirmed, trimmed display name. Valid only when <see cref="ShowDialog"/> returned true.</summary>
    public string NewName { get; private set; } = string.Empty;

    public RenameWindow(string? currentName = null)
    {
        InitializeComponent();

        NameBox.MaxLength = IndexStore.MaxDisplayNameLength;
        NameBox.Text = currentName?.Trim() ?? string.Empty;
        NameBox.SelectAll();
        NameBox.Focus();
        UpdateRenameEnabled();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateRenameEnabled();

    private void UpdateRenameEnabled()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var valid = name.Length > 0 && name.Length <= IndexStore.MaxDisplayNameLength;

        if (RenameButton is not null)
            RenameButton.IsEnabled = valid;

        if (ValidationText is not null)
        {
            if (name.Length == 0)
            {
                ValidationText.Text = "Enter a name.";
                ValidationText.Visibility = Visibility.Visible;
            }
            else if (name.Length > IndexStore.MaxDisplayNameLength)
            {
                ValidationText.Text = $"Keep the name to {IndexStore.MaxDisplayNameLength} characters or fewer.";
                ValidationText.Visibility = Visibility.Visible;
            }
            else
            {
                ValidationText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > IndexStore.MaxDisplayNameLength)
        {
            UpdateRenameEnabled();
            return;
        }

        NewName = name;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
