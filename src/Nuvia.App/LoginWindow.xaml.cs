using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nuvia.App.Services;
using QRCoder;

namespace Nuvia.App;

// Kept for backwards compatibility with earlier references; the window now drives
// itself with the private Step enum below (single-screen progressive flow).
public enum LoginState
{
    PhoneEntry,
    CodeEntry,
    PasswordEntry,
    Busy,
    Error,
    Completed
}

public partial class LoginWindow : Window
{
    private enum Step { Phone, Code, Password }

    // App is for India — start the phone field on the +91 dialling code.
    private const string DefaultPhone = "+91 ";

    private readonly IAuthService _auth;
    private Step _step = Step.Phone;
    private bool _busy;
    private bool _completed;
    private CancellationTokenSource? _cts;
    private Action? _onConfigErrorRetry;

    // Resend / SMS fallback: a 1-second timer drives the live cooldown countdown on the code step.
    private readonly DispatcherTimer _resendTimer;

    // QR sign-in: runs alongside (never replaces) the phone flow. Its own token so backing out or
    // refreshing the code cancels only the QR attempt, leaving the phone-flow _cts untouched.
    private bool _qrMode;
    private CancellationTokenSource? _qrCts;

    public event Action? AuthenticationCompleted;

    public LoginWindow(IAuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        _cts = new CancellationTokenSource();

        // Set the prefix here, not in XAML: a Text set during InitializeComponent raises
        // TextChanged while the visual tree is still half-built and would throw.
        PhoneTextBox.Text = DefaultPhone;
        PhoneTextBox.CaretIndex = PhoneTextBox.Text.Length;

        // The demo notice must only ever appear when the demo stand-in is actually in use.
        DemoNotice.Visibility = _auth.IsDemoMode ? Visibility.Visible : Visibility.Collapsed;

        // Drive the resend cooldown countdown; created before GoToStep so the first
        // ApplyStep()/UpdateResendUi() can safely touch it.
        _resendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _resendTimer.Tick += ResendTimer_Tick;

        // Only wire QR when the active service actually supports it (never in demo / unconfigured modes).
        if (_auth.SupportsQrLogin)
            _auth.QrPasswordRequired += OnQrPasswordRequired;

        GoToStep(Step.Phone);
    }

    // Advance to a step, revealing (never hiding) its field so earlier fields stay
    // on screen — the whole phone → code → 2FA flow lives on this one window.
    private void GoToStep(Step step)
    {
        _step = step;

        if (step == Step.Code) CodePanel.Visibility = Visibility.Visible;
        if (step == Step.Password) PasswordPanel.Visibility = Visibility.Visible;

        ApplyStep();

        switch (step)
        {
            case Step.Phone:
                PhoneTextBox.Focus();
                PhoneTextBox.CaretIndex = PhoneTextBox.Text.Length;
                break;
            case Step.Code:
                CodeTextBox.Focus();
                UpdateCodeMediumHint();
                break;
            case Step.Password:
                PasswordBox.Focus();
                break;
        }
    }

    // Sync the button label and which input is editable to the current step.
    private void ApplyStep()
    {
        // Only the active step accepts edits; earlier steps stay visible but locked
        // (Start over unlocks everything and returns to the phone field).
        PhoneTextBox.IsEnabled = _step == Step.Phone && !_busy;
        CodeTextBox.IsEnabled = _step == Step.Code && !_busy;
        PasswordBox.IsEnabled = _step == Step.Password && !_busy;

        PrimaryActionButton.Content = _step switch
        {
            Step.Phone => "Send code",
            Step.Code => "Verify",
            Step.Password => "Sign in",
            _ => "Continue"
        };

        StartOverButton.Visibility = _step == Step.Phone ? Visibility.Collapsed : Visibility.Visible;

        // The QR alternative belongs to the very first (phone) step only — never once a code
        // is in flight, while busy, or while already in QR mode.
        QrEntryPanel.Visibility =
            (!_qrMode && !_busy && _step == Step.Phone && _auth.SupportsQrLogin)
                ? Visibility.Visible : Visibility.Collapsed;

        // Keep the resend control (and its countdown) in sync with the current step/busy state.
        UpdateResendUi();
    }

    // Inline busy indicator — the form stays visible; no full-screen takeover.
    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        BusyText.Text = message;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryActionButton.IsEnabled = !busy;
        StartOverButton.IsEnabled = !busy;
        ApplyStep();
    }

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Phone: await SubmitPhoneAsync(); break;
            case Step.Code: await SubmitCodeAsync(); break;
            case Step.Password: await SubmitPasswordAsync(); break;
        }
    }

    private async Task SubmitPhoneAsync()
    {
        var phone = PhoneTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(phone) || phone == "+")
        {
            PhoneErrorText.Text = "Please enter your phone number.";
            PhoneErrorText.Visibility = Visibility.Visible;
            return;
        }

        PhoneErrorText.Visibility = Visibility.Collapsed;
        SetBusy(true, "Sending code…");

        string? error;
        try
        {
            error = await _auth.RequestCodeAsync(phone, _cts!.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            return;
        }

        SetBusy(false);

        if (error == null)
        {
            // Some accounts jump straight to 2FA; most go to the code step first.
            GoToStep(_auth.RequiresPassword ? Step.Password : Step.Code);
        }
        else
        {
            PhoneErrorText.Text = error;
            PhoneErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task SubmitCodeAsync()
    {
        var code = CodeTextBox.Text.Trim();
        // Delivery method varies (app/SMS/call) and so does code length — accept 4–8 digits.
        if (code.Length < 4 || code.Length > 8 || !code.All(char.IsDigit))
        {
            CodeErrorText.Text = "Enter the code you received (4–8 digits).";
            CodeErrorText.Visibility = Visibility.Visible;
            return;
        }

        CodeErrorText.Visibility = Visibility.Collapsed;
        SetBusy(true, "Verifying code…");

        string? error;
        try
        {
            error = await _auth.VerifyCodeAsync(code, _cts!.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            return;
        }

        SetBusy(false);

        if (error == null)
        {
            if (_auth.RequiresPassword)
                GoToStep(Step.Password);
            else
                Complete();
        }
        else
        {
            CodeErrorText.Text = error;
            CodeErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task SubmitPasswordAsync()
    {
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            PasswordErrorText.Text = "Enter your 2FA password.";
            PasswordErrorText.Visibility = Visibility.Visible;
            return;
        }

        PasswordErrorText.Visibility = Visibility.Collapsed;
        SetBusy(true, "Signing in…");

        string? error;
        try
        {
            error = await _auth.SubmitPasswordAsync(password, _cts!.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            return;
        }

        SetBusy(false);

        if (error == null)
            Complete();
        else
        {
            PasswordErrorText.Text = error;
            PasswordErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Complete()
    {
        _completed = true;
        AuthenticationCompleted?.Invoke();
        Close();
    }

    private void PhoneTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var text = PhoneTextBox.Text + e.Text;
        e.Handled = !IsValidPhoneInput(text);
    }

    private void PhoneTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged also fires while InitializeComponent() builds the tree, before the
        // error label exists — guard so window construction can never throw.
        if (PhoneErrorText is null || PhoneTextBox is null)
            return;

        PhoneErrorText.Visibility = Visibility.Collapsed;
        var text = PhoneTextBox.Text;
        if (!text.StartsWith("+"))
        {
            PhoneTextBox.Text = "+";
            PhoneTextBox.CaretIndex = 1;
        }
    }

    private static bool IsValidPhoneInput(string text)
    {
        if (!text.StartsWith("+")) return false;
        foreach (var c in text.AsSpan(1))
        {
            if (!char.IsDigit(c) && c != ' ') return false;
        }
        return true;
    }

    private void CodeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !char.IsDigit(e.Text[0]);
    }

    private void CodeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CodeErrorText is null)
            return;

        CodeErrorText.Visibility = Visibility.Collapsed;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordErrorText.Visibility = Visibility.Collapsed;
    }

    private void StartOverButton_Click(object sender, RoutedEventArgs e)
    {
        // Forget the in-progress login and return to the phone step, but keep the
        // number the user typed so they can correct it.
        _auth.Reset();

        CodeTextBox.Clear();
        PasswordBox.Clear();
        PhoneErrorText.Visibility = Visibility.Collapsed;
        CodeErrorText.Visibility = Visibility.Collapsed;
        PasswordErrorText.Visibility = Visibility.Collapsed;

        CodePanel.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Collapsed;

        SetBusy(false);
        GoToStep(Step.Phone);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _auth.Reset();
        PhoneTextBox.Clear();
        CodeTextBox.Clear();
        PasswordBox.Clear();
        Close();
    }

    // ============================ Resend / SMS fallback ============================

    // Tell the user where the current code went (app / SMS / call). Never a secret.
    private void UpdateCodeMediumHint()
    {
        if (CodeHint is null) return;
        var medium = _auth.CurrentCodeMedium;
        CodeHint.Text = string.IsNullOrEmpty(medium)
            ? "Enter the code you received."
            : $"Enter the code sent via {medium}.";
    }

    // Show/enable the resend control and drive its live countdown. Centralises the timer:
    // starts it while a cooldown remains, stops it when a resend is possible now or not offered.
    private void UpdateResendUi()
    {
        if (ResendButton is null) return;

        if (_qrMode || !_auth.CanResendCode)
        {
            ResendButton.Visibility = Visibility.Collapsed;
            _resendTimer?.Stop();
            return;
        }

        ResendButton.Visibility = Visibility.Visible;
        var next = _auth.NextCodeMedium;
        var cooldown = _auth.ResendCooldownSeconds;

        if (cooldown > 0)
        {
            ResendButton.IsEnabled = false;
            ResendButton.Content = string.IsNullOrEmpty(next)
                ? $"Resend code in {cooldown}s"
                : $"Resend via {next} in {cooldown}s";
            _resendTimer?.Start();
        }
        else
        {
            ResendButton.IsEnabled = !_busy;
            ResendButton.Content = string.IsNullOrEmpty(next)
                ? "Resend code"
                : $"Resend code via {next}";
            _resendTimer?.Stop();
        }
    }

    private void ResendTimer_Tick(object? sender, EventArgs e)
    {
        // Only meaningful on the code step; UpdateResendUi stops the timer once the cooldown ends.
        if (_step != Step.Code || _qrMode)
        {
            _resendTimer?.Stop();
            return;
        }
        UpdateResendUi();
    }

    private async void ResendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        ResendButton.IsEnabled = false;
        CodeErrorText.Visibility = Visibility.Collapsed;
        SetBusy(true, "Requesting a new code…");

        string? error;
        try
        {
            error = await _auth.ResendCodeAsync(_cts!.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            return;
        }

        SetBusy(false);

        if (error == null)
        {
            // A fresh code really went out (the service confirms it before reporting success).
            CodeTextBox.Clear();
            UpdateCodeMediumHint();
        }
        else
        {
            CodeErrorText.Text = error;
            CodeErrorText.Visibility = Visibility.Visible;
        }

        // Refresh the label/countdown against the new cooldown (SetBusy already re-ran ApplyStep).
        UpdateResendUi();
    }

    // ================================ QR sign-in ================================

    private void QrLoginButton_Click(object sender, RoutedEventArgs e)
    {
        EnterQrMode();
        _ = StartQrLoginAsync();
    }

    // Swap the phone flow out of view for the QR panel. The phone-flow state is left intact
    // (hidden, not reset) so backing out returns the user exactly where they were.
    private void EnterQrMode()
    {
        _qrMode = true;
        PhoneFlowRoot.Visibility = Visibility.Collapsed;
        QrFlowPanel.Visibility = Visibility.Visible;
        ResetQrPanelToLoading();
    }

    private void ResetQrPanelToLoading()
    {
        QrImage.Source = null;
        QrTile.Visibility = Visibility.Visible;
        QrPlaceholder.Visibility = Visibility.Visible;
        QrStatusText.Text = "Preparing a secure QR code…";
        QrStatusText.Visibility = Visibility.Visible;
        QrPasswordPanel.Visibility = Visibility.Collapsed;
        QrPasswordBox.Clear();
        QrPasswordError.Visibility = Visibility.Collapsed;
        QrErrorText.Visibility = Visibility.Collapsed;
        QrRetryButton.Visibility = Visibility.Collapsed;
    }

    private async Task StartQrLoginAsync()
    {
        // Fresh token for this attempt; cancelling it abandons only the QR flow.
        _qrCts?.Cancel();
        _qrCts?.Dispose();
        _qrCts = new CancellationTokenSource();
        var ct = _qrCts.Token;

        string? error;
        try
        {
            // The URL callback fires on a background thread — marshal every render to the UI.
            error = await _auth.LoginWithQrAsync(
                url => Dispatcher.Invoke(() => RenderQr(url)), ct);
        }
        catch (OperationCanceledException)
        {
            return; // backed out, refreshing, or the window is closing
        }
        catch (Exception)
        {
            if (_qrMode && !ct.IsCancellationRequested)
                ShowQrError("QR sign-in failed. Please try again, or use phone sign-in.");
            return;
        }

        if (ct.IsCancellationRequested || !_qrMode)
            return;

        if (error == null)
            Complete();
        else
            ShowQrError(error);
    }

    // Render a tg://login token as a scannable QR image. PngByteQRCode has no System.Drawing
    // dependency, so this is safe on any Windows host.
    private void RenderQr(string url)
    {
        if (!_qrMode) return;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(20);

            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();

            QrImage.Source = bmp;
            QrTile.Visibility = Visibility.Visible;
            QrPlaceholder.Visibility = Visibility.Collapsed;
            QrErrorText.Visibility = Visibility.Collapsed;
            QrRetryButton.Visibility = Visibility.Collapsed;
            QrStatusText.Text = "Scan this code in the Telegram app on your phone.";
            QrStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            ShowQrError("Could not display the QR code. Please use phone sign-in instead.");
        }
    }

    private void ShowQrError(string message)
    {
        QrErrorText.Text = message;
        QrErrorText.Visibility = Visibility.Visible;
        QrStatusText.Visibility = Visibility.Collapsed;
        QrPlaceholder.Visibility = Visibility.Collapsed;
        QrRetryButton.Visibility = Visibility.Visible;
        QrPasswordSubmit.IsEnabled = true;
    }

    // Raised on a background thread by the auth service when the account has 2FA — marshal to the UI.
    private void OnQrPasswordRequired(int attempt)
    {
        try
        {
            Dispatcher.Invoke(() => ShowQrPasswordPrompt(attempt));
        }
        catch (Exception)
        {
            // The window is tearing down; the cancelled token will unwind the QR flow.
        }
    }

    private void ShowQrPasswordPrompt(int attempt)
    {
        if (!_qrMode) return;

        // Once 2FA is requested the QR token has already been accepted, so the code is no longer
        // scannable — hide the tile and focus the password field instead.
        QrTile.Visibility = Visibility.Collapsed;
        QrPlaceholder.Visibility = Visibility.Collapsed;
        QrErrorText.Visibility = Visibility.Collapsed;
        QrRetryButton.Visibility = Visibility.Collapsed;

        QrStatusText.Text = "Almost there — this account has two-factor authentication.";
        QrStatusText.Visibility = Visibility.Visible;

        QrPasswordPanel.Visibility = Visibility.Visible;
        QrPasswordSubmit.IsEnabled = true;
        QrPasswordBox.Clear();
        QrPasswordBox.Focus();

        // attempt > 1 means the previous password was wrong.
        if (attempt > 1)
        {
            QrPasswordError.Text = "Incorrect password. Please try again.";
            QrPasswordError.Visibility = Visibility.Visible;
        }
        else
        {
            QrPasswordError.Visibility = Visibility.Collapsed;
        }
    }

    private void QrPasswordSubmit_Click(object sender, RoutedEventArgs e)
    {
        var pw = QrPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(pw))
        {
            QrPasswordError.Text = "Enter your 2FA password.";
            QrPasswordError.Visibility = Visibility.Visible;
            return;
        }

        QrPasswordError.Visibility = Visibility.Collapsed;
        QrPasswordSubmit.IsEnabled = false;
        QrStatusText.Text = "Signing in…";
        QrStatusText.Visibility = Visibility.Visible;

        // Hand the password to the blocked callback; StartQrLoginAsync then completes, or
        // OnQrPasswordRequired fires again (attempt > 1) if it was wrong.
        _auth.ProvideQrPassword(pw);
        QrPasswordBox.Clear();
    }

    private void QrPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (QrPasswordError is not null)
            QrPasswordError.Visibility = Visibility.Collapsed;
    }

    private void QrRetryButton_Click(object sender, RoutedEventArgs e)
    {
        // Abandon the current QR attempt and request a brand-new code.
        ResetQrPanelToLoading();
        _ = StartQrLoginAsync();
    }

    private void QrBackButton_Click(object sender, RoutedEventArgs e)
    {
        ExitQrMode();
    }

    // Leave QR and return to a clean phone step, mirroring "Start over" in the phone flow.
    private void ExitQrMode()
    {
        _qrMode = false;
        _qrCts?.Cancel();
        _auth.Reset();

        QrPasswordBox.Clear();
        QrImage.Source = null;

        QrFlowPanel.Visibility = Visibility.Collapsed;
        PhoneFlowRoot.Visibility = Visibility.Visible;

        SetBusy(false);
        GoToStep(Step.Phone);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        // Only reachable from the configuration-error screen — return to the sign-in form.
        ErrorPanel.Visibility = Visibility.Collapsed;
        FormScroller.Visibility = Visibility.Visible;

        _auth.Reset();
        PhoneTextBox.Text = DefaultPhone;
        CodeTextBox.Clear();
        PasswordBox.Clear();
        PhoneErrorText.Visibility = Visibility.Collapsed;
        CodeErrorText.Visibility = Visibility.Collapsed;
        PasswordErrorText.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Collapsed;
        SetBusy(false);
        GoToStep(Step.Phone);

        // A startup/configuration failure retries the startup path, not just the form.
        if (_onConfigErrorRetry is { } retry)
        {
            _onConfigErrorRetry = null;
            retry();
        }
    }

    /// <summary>
    /// Show a safe configuration error instead of any credential form. Never includes
    /// credential values or file paths beyond what the message text states.
    /// </summary>
    public void ShowConfigurationError(string message, Action? onRetry = null)
    {
        _onConfigErrorRetry = onRetry;
        ErrorMessageText.Text = message;
        SetBusy(false);
        FormScroller.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Always wipe in-memory secrets, whether or not sign-in succeeded.
        CodeTextBox.Clear();
        PasswordBox.Clear();
        PhoneTextBox.Clear();
        QrPasswordBox.Clear();

        // Tear down the resend countdown and QR wiring before anything else touches the auth service.
        _resendTimer.Stop();
        _resendTimer.Tick -= ResendTimer_Tick;
        if (_auth.SupportsQrLogin)
            _auth.QrPasswordRequired -= OnQrPasswordRequired;
        _qrCts?.Cancel();
        _qrCts?.Dispose();
        _qrCts = null;

        if (!_completed)
        {
            _cts?.Cancel();
            _auth.Reset();
            _auth.Dispose();
        }

        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // QR mode has its own keys: Enter submits the 2FA password (when shown), Escape backs out.
        if (_qrMode)
        {
            if (e.Key == Key.Enter)
            {
                if (QrPasswordPanel.Visibility == Visibility.Visible && QrPasswordSubmit.IsEnabled)
                    QrPasswordSubmit_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ExitQrMode();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (ErrorPanel.Visibility == Visibility.Visible)
                RetryButton_Click(sender, new RoutedEventArgs());
            else if (!_busy)
                PrimaryActionButton_Click(sender, new RoutedEventArgs());

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_busy)
            {
                // Ignore Escape while a request is in flight.
            }
            else if (ErrorPanel.Visibility == Visibility.Visible)
            {
                RetryButton_Click(sender, new RoutedEventArgs());
            }
            else if (_step != Step.Phone)
            {
                // Step back to the phone field rather than closing the window.
                StartOverButton_Click(sender, new RoutedEventArgs());
            }
            else
            {
                CancelButton_Click(sender, new RoutedEventArgs());
            }

            e.Handled = true;
        }
    }
}
