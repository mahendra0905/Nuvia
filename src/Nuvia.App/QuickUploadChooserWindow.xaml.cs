using System.IO;
using System.Windows;

namespace Nuvia.App;

/// <summary>The destination a user picked in the quick-upload chooser.</summary>
public enum QuickUploadChoice
{
    /// <summary>The user closed or cancelled the chooser: do nothing.</summary>
    Cancel,

    /// <summary>Send the file to Saved Messages.</summary>
    SavedMessages,

    /// <summary>Send the file to the existing managed private group.</summary>
    ExistingGroup,

    /// <summary>Create a new private group first, then upload to it.</summary>
    CreateGroup,
}

/// <summary>
/// Small green-accent chooser shown for a shell-launched quick upload when the destination is left open
/// ("Choose destination…"), or whenever a hint cannot be resolved without asking. It is a passive picker:
/// it touches neither Telegram nor the index and creates nothing — it only reports which destination the
/// user picked. The single managed-group rule is honoured by the caller, which only offers "create" when
/// no group exists yet.
/// </summary>
public partial class QuickUploadChooserWindow : Window
{
    private QuickUploadChooserWindow()
    {
        InitializeComponent();
    }

    /// <summary>The user's choice; defaults to <see cref="QuickUploadChoice.Cancel"/> until a button is clicked.</summary>
    public QuickUploadChoice Choice { get; private set; } = QuickUploadChoice.Cancel;

    /// <summary>
    /// Shows the chooser modally and returns the picked destination. The existing group is offered only
    /// when <paramref name="existingGroupTitle"/> is non-null; "create a new group" is offered only when
    /// <paramref name="canCreate"/> is true. Both are hidden otherwise, so the two never appear together.
    /// </summary>
    public static QuickUploadChoice Ask(Window? owner, string? filePath, string? existingGroupTitle, bool canCreate)
    {
        var window = new QuickUploadChooserWindow();
        if (owner is not null && owner.IsVisible)
            window.Owner = owner;

        if (!string.IsNullOrWhiteSpace(filePath))
            window.SetFile(filePath!);

        if (!string.IsNullOrWhiteSpace(existingGroupTitle))
        {
            window.GroupButton.Content = $"Save to “{existingGroupTitle}”";
            window.GroupButton.Visibility = Visibility.Visible;
        }

        if (canCreate)
            window.CreateButton.Visibility = Visibility.Visible;

        window.ShowDialog();
        return window.Choice;
    }

    /// <summary>Shows the file name being uploaded (never a full path — just the leaf name).</summary>
    public void SetFile(string path)
    {
        var name = Path.GetFileName(path);
        FileText.Text = string.IsNullOrWhiteSpace(name)
            ? "Choose where to store this file."
            : $"Choose where to store “{name}”.";
    }

    private void SavedButton_Click(object sender, RoutedEventArgs e) => Pick(QuickUploadChoice.SavedMessages);

    private void GroupButton_Click(object sender, RoutedEventArgs e) => Pick(QuickUploadChoice.ExistingGroup);

    private void CreateButton_Click(object sender, RoutedEventArgs e) => Pick(QuickUploadChoice.CreateGroup);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Pick(QuickUploadChoice.Cancel);

    private void Pick(QuickUploadChoice choice)
    {
        Choice = choice;
        DialogResult = choice != QuickUploadChoice.Cancel;
        Close();
    }
}
