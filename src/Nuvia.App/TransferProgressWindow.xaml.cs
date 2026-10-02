using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Nuvia.App;

/// <summary>
/// A separate, Windows Explorer-style progress window shown while a transfer runs, replacing the old
/// inline transfer strip inside the main window.
/// <para>
/// The window is a passive view: it never touches Telegram, the index, or the cancellation token. The
/// owner (<see cref="MainWindow"/>) drives it through <see cref="Begin"/>/<see cref="Report"/> and
/// listens to <see cref="CancelRequested"/> to stop the transfer. While a transfer is live the window
/// refuses to close on its own — closing it (or pressing Cancel) is a <em>cancel request</em>; the owner
/// closes the window with <see cref="ForceClose"/> once the coordinator reports the terminal state, so a
/// stray close can never orphan a partial file.
/// </para>
/// </summary>
public partial class TransferProgressWindow : Window
{
    /// <summary>Raised when the user asks to stop the transfer (Cancel button or the window's X).</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Raised when the user clicks Pause/Resume; the owner toggles the coordinator accordingly.</summary>
    public event EventHandler? PauseResumeRequested;

    // Segoe Fluent / MDL2 glyphs for the pause and resume (play) states.
    private const string PauseGlyph = "";
    private const string ResumeGlyph = "";

    // A paused transfer turns the bar amber (like a native copy dialog); running is the green accent.
    private static readonly Brush PausedBrush = CreateFrozen(Color.FromRgb(0xF5, 0xA6, 0x23));

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool _forceClose;
    private bool _cancellable;
    private bool _cancelRaised;
    private Brush? _runningBrush;
    // Debounce: a Pause/Resume click disables the button until the coordinator reports the new state
    // (via SetPause). This stops rapid "pause-resume-pause" hammering from storming the transfer.
    private bool _toggling;

    public TransferProgressWindow()
    {
        InitializeComponent();
        _runningBrush = Progress.Foreground;
    }

    /// <summary>Starts a new operation: resets progress, cancel state, and the flood-wait note.</summary>
    public void Begin(string header, bool cancellable, bool indeterminate)
    {
        _cancellable = cancellable;
        _cancelRaised = false;
        HeaderText.Text = header;
        DetailText.Text = indeterminate ? "Working…" : "Starting…";
        Progress.IsIndeterminate = indeterminate;
        Progress.Value = 0;
        Progress.Foreground = _runningBrush;
        FloodPanel.Visibility = Visibility.Collapsed;
        FloodText.Text = string.Empty;
        Title = "Nuvia";
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = cancellable;
        // The pause button only appears once bytes start moving (SetPause from a running snapshot).
        PauseButton.Visibility = Visibility.Collapsed;
        PauseButton.IsEnabled = true;
        _toggling = false;
    }

    /// <summary>Live progress update, driven from the owner's transfer snapshot.</summary>
    public void Report(string header, string detail, double fraction, bool indeterminate)
    {
        HeaderText.Text = header;
        DetailText.Text = detail;
        Progress.IsIndeterminate = indeterminate;

        if (indeterminate)
        {
            Title = "Nuvia";
        }
        else
        {
            var clamped = Math.Clamp(fraction, 0d, 1d);
            Progress.Value = clamped;
            Title = string.Create(CultureInfo.InvariantCulture, $"{clamped * 100:0}% complete");
        }
    }

    /// <summary>Shows or hides the Cancel button as the transfer's running state changes.</summary>
    public void SetCancellable(bool visible, bool enabled)
    {
        CancelButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Shows or hides the Pause/Resume button and reflects the current pause state: while paused the
    /// button becomes a green "Resume" (play) and the bar turns amber; while running it is "Pause".
    /// </summary>
    public void SetPause(bool visible, bool paused)
    {
        if (!visible)
        {
            PauseButton.Visibility = Visibility.Collapsed;
            Progress.Foreground = _runningBrush;
            _toggling = false;
            return;
        }

        PauseButton.Visibility = Visibility.Visible;
        PauseIcon.Text = paused ? ResumeGlyph : PauseGlyph;
        PauseLabel.Text = paused ? "Resume" : "Pause";
        PauseButton.ToolTip = paused ? "Resume the current transfer" : "Pause the current transfer";
        Progress.Foreground = paused ? PausedBrush : _runningBrush;
        // The new state has now been reported: accept the next click.
        _toggling = false;
        PauseButton.IsEnabled = true;
    }

    /// <summary>Shows or clears the flood-wait note (null/empty hides it).</summary>
    public void SetFloodWait(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            FloodPanel.Visibility = Visibility.Collapsed;
            FloodText.Text = string.Empty;
        }
        else
        {
            FloodText.Text = message;
            FloodPanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Reflects the "cancelling…" state after the user has asked to stop.</summary>
    public void ShowCancelling()
    {
        CancelButton.IsEnabled = false;
        // No point pausing something that is being torn down; hide the button and restore the bar colour.
        PauseButton.Visibility = Visibility.Collapsed;
        Progress.Foreground = _runningBrush;
        DetailText.Text = "Cancelling — cleaning up…";
    }

    /// <summary>Closes the window even while a transfer is live. For the owner only.</summary>
    public void ForceClose()
    {
        if (_forceClose)
            return;

        // Set the hard-close flag so OnClosing lets the real close through.
        _forceClose = true;

        // No fade-out at the end of a transfer (the user asked for that dark fade to go). Hide the window
        // before destroying it: closing a visible, hardware-accelerated WPF window makes DWM paint its raw
        // (black) Win32 surface for a frame or two while the render target is torn down — the "black blink".
        // Hiding first (SW_HIDE) lets the window leave the desktop composition cleanly, so the following
        // Close() destroys an already off-screen window with no flash and no fade.
        Hide();
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => RaiseCancel();

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        // Ignore a click while the previous toggle is still being confirmed (debounce).
        if (_toggling)
            return;

        _toggling = true;
        PauseButton.IsEnabled = false;
        PauseResumeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCancel()
    {
        if (!_cancellable || _cancelRaised)
            return;

        _cancelRaised = true;
        ShowCancelling();
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }

        // The user closing the window is a cancel request, not a hard close: keep it up so the
        // "cancelling…" state stays visible, and let the owner close it once cleanup finishes.
        e.Cancel = true;
        if (_cancellable)
            RaiseCancel();
    }
}
