using System.Windows;
using System.Windows.Input;

namespace Nuvia.App;

/// <summary>
/// A small, chrome-less splash shown only for a returning user while a stored session is silently
/// resumed. It replaces the full sign-in window during that brief probe, so a signed-in user goes
/// from launch straight to the main window without the "Nuvia — Sign In" screen flashing.
/// If resume fails, the caller closes this and shows the real sign-in window instead.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>Allow the borderless window to be dragged, so it can never trap the pointer.</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
