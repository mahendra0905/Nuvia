using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Nuvia.App;

/// <summary>
/// A small, passive end-of-batch summary dialog. It is shown once, only when some files in a multi-file
/// upload or download ran into problems, listing each affected file with a short reason. It holds no
/// services and performs no network, index or file work itself — the caller does everything and simply
/// presents the outcome here, mirroring the value-only dialog style used elsewhere in the app.
/// </summary>
public partial class TransferSummaryWindow : Window
{
    /// <summary>One problem row: the file's display name and a short, already-safe reason.</summary>
    public sealed record Problem(string Name, string Reason);

    /// <param name="owner">The window to centre over.</param>
    /// <param name="title">Short heading, e.g. "Upload finished with issues".</param>
    /// <param name="summary">The one-line status already shown in the status bar.</param>
    /// <param name="problems">The files that failed or could not be indexed, with reasons.</param>
    public TransferSummaryWindow(Window owner, string title, string summary,
        IReadOnlyList<(string Name, string Reason)> problems)
    {
        InitializeComponent();

        Owner = owner;
        TitleText.Text = title;
        SummaryText.Text = summary;
        ProblemList.ItemsSource = problems.Select(p => new Problem(p.Name, p.Reason)).ToList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
