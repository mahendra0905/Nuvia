using System.Windows;

namespace Nuvia.App;

/// <summary>
/// Native confirmation dialog for creating — or renaming — the one optional private storage group.
/// <para>
/// This window only collects and confirms a name. It never touches Telegram: the caller performs the
/// create or rename only if <see cref="ShowDialog"/> returns <c>true</c>, so nothing changes on the
/// account without this explicit confirmation.
/// </para>
/// </summary>
public partial class CreateGroupWindow : Window
{
    /// <summary>The confirmed, trimmed group name. Valid only when <see cref="ShowDialog"/> returned true.</summary>
    public string GroupName { get; private set; } = string.Empty;

    public CreateGroupWindow(string? suggestedName = null, bool renameMode = false)
    {
        InitializeComponent();

        if (renameMode)
        {
            // Rename mode: this edits ONLY the group's own title on Telegram. It renames no stored file
            // or message (Nuvia's file "rename" is separate and local). Make that plain in the copy.
            Title = "Rename storage group";
            HeadingText.Text = "Rename your private storage group";
            DescriptionText.Text =
                "This changes the group's name on your own Telegram account. It only renames the group "
                + "itself — none of your stored files or messages are edited, and no one is added or invited.";
            CreateButton.Content = "Rename";
        }

        NameBox.Text = string.IsNullOrWhiteSpace(suggestedName) ? "Nuvia storage" : suggestedName.Trim();
        NameBox.SelectAll();
        NameBox.Focus();
        UpdateCreateEnabled();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateCreateEnabled();

    private void UpdateCreateEnabled()
    {
        var hasName = !string.IsNullOrWhiteSpace(NameBox.Text);
        if (CreateButton is not null)
            CreateButton.IsEnabled = hasName;
        if (ValidationText is not null)
            ValidationText.Visibility = hasName ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        GroupName = name;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
