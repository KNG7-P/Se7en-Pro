using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.Services.Foxy;

namespace Se7enPro.Views;

public partial class AddFoxyAccountDialog : Window
{
    private enum WizardStep
    {
        Protocol = 0,
        MethodChoice = 1,
        Credentials = 2,
    }

    private readonly FoxyAccountService _accountService;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _transportCts;
    private FoxyLoginTransportSession? _transportSession;

    private WizardStep _step = WizardStep.Protocol;
    private bool _busy;
    private bool _awaitingCode;
    private string _email = "";
    private string _sessionToken = "";
    private TaskCompletionSource<string>? _captchaTcs;

    public FirefoxAccountInfo? Result { get; private set; }

    public AddFoxyAccountDialog(FoxyAccountService accountService)
    {
        InitializeComponent();
        ModalWindowHelper.AttachCentered(this);
        _accountService = accountService;

        Loaded += (_, _) =>
        {
            ApplyStep(WizardStep.Protocol);
            if (Owner is MainWindow mw) mw.ShowBackdrop();
            else if (Application.Current?.MainWindow is MainWindow main) main.ShowBackdrop();
        };
        Closed += (_, _) =>
        {
            _captchaTcs?.TrySetCanceled();
            CancelTransportAttempt();
            try { _cts.Cancel(); } catch { }
            ReleaseTransportSessionAsync();
            if (Owner is MainWindow mw) mw.HideBackdrop();
            else if (Application.Current?.MainWindow is MainWindow main) main.HideBackdrop();
        };
    }

    private void ApplyStep(WizardStep step)
    {
        _step = step;
        ErrorBorder.Visibility = Visibility.Collapsed;

        ProtocolStepPanel.Visibility = step == WizardStep.Protocol ? Visibility.Visible : Visibility.Collapsed;
        MethodChoicePanel.Visibility = step == WizardStep.MethodChoice ? Visibility.Visible : Visibility.Collapsed;
        CredentialsStepPanel.Visibility = step == WizardStep.Credentials ? Visibility.Visible : Visibility.Collapsed;

        if (step == WizardStep.Protocol)
        {
            PrimaryButton.Visibility = Visibility.Visible;
            UpdateProtocolPrimaryLabel();
            TransportComboBox.Focus();
            return;
        }

        if (step == WizardStep.MethodChoice)
        {
            PrimaryButton.Visibility = Visibility.Collapsed;
            DirectSignInButton.Visibility = IsAetherTransportActive() ? Visibility.Collapsed : Visibility.Visible;
            var label = _transportSession?.DisplayLabel ?? "Direct";
            var hasTempProxy = !string.IsNullOrWhiteSpace(_transportSession?.SocksProxyEndpoint);
            MethodTransportText.Text = label;
            MethodTransportIcon.Kind = hasTempProxy ? PackIconKind.CheckCircleOutline : PackIconKind.ShieldCheckOutline;
            MethodTransportIcon.Foreground = hasTempProxy
                ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
                : (Brush)FindResource("TextSecondaryBrush");
            return;
        }

        PrimaryButton.Visibility = Visibility.Visible;
        PrimaryButton.Content = _awaitingCode ? Loc.Of("Verify") : Loc.Of("Loc_foxySignIn");
        if (PrimaryButton.Content is not string s || string.IsNullOrEmpty(s) || s == "Loc_foxySignIn")
            PrimaryButton.Content = TryFindResource("Loc_foxySignIn") ?? "Sign in";

        CredentialsTransportText.Text = _transportSession?.DisplayLabel ?? "Direct";
        BackToMethodButton.Visibility = _awaitingCode ? Visibility.Collapsed : Visibility.Visible;
        if (!_awaitingCode)
            EmailInput.Focus();
    }

    private bool IsAetherTransportActive()
    {
        var kind = _transportSession?.Kind ?? SelectedTransportKind;
        if (kind is FoxyLoginTransportKind.Masque or FoxyLoginTransportKind.WireGuard)
            return true;
        if (kind == FoxyLoginTransportKind.None &&
            _accountService.ActiveMainTunnelMethod is ConnectionMethod.Masque or ConnectionMethod.WireGuard
                or ConnectionMethod.WarpOnWarp or ConnectionMethod.MasqueInMasque or ConnectionMethod.WarpOverV2Ray)
            return true;
        return false;
    }

    private FoxyLoginTransportKind SelectedTransportKind
    {
        get
        {
            if (TransportComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
                Enum.TryParse<FoxyLoginTransportKind>(tag, out var kind))
            {
                return kind;
            }
            return FoxyLoginTransportKind.None;
        }
    }

    private void UpdateProtocolPrimaryLabel()
    {
        PrimaryButton.Content = SelectedTransportKind == FoxyLoginTransportKind.None
            ? "Continue"
            : "Connect & Continue";
    }

    private void OnTransportSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        ErrorBorder.Visibility = Visibility.Collapsed;
        TransportStatusCard.Visibility = Visibility.Collapsed;
        TransportConnectingRing.Visibility = Visibility.Collapsed;
        TransportConnectedBadge.Visibility = Visibility.Collapsed;
        UpdateProtocolPrimaryLabel();

        TransportHintText.Text = SelectedTransportKind switch
        {
            FoxyLoginTransportKind.None => "Uses direct connection or active VPN.",
            FoxyLoginTransportKind.Lantern => "Temporary Lantern proxy for sign-in.",
            FoxyLoginTransportKind.Veep => "Temporary Veep proxy for sign-in.",
            FoxyLoginTransportKind.V2Ray => DescribeV2RayHint(),
            FoxyLoginTransportKind.WireGuard => "Temporary WireGuard proxy for sign-in.",
            FoxyLoginTransportKind.Masque => "Temporary MASQUE proxy for sign-in.",
            _ => "",
        };
    }

    private string DescribeV2RayHint()
    {
        var node = _accountService.DescribeConfiguredV2RayNode();
        return string.IsNullOrEmpty(node)
            ? "Select a V2Ray config in Settings first."
            : "Uses your selected V2Ray config.";
    }

    private void OnChangeTransportClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ReleaseTransportSessionAsync();
        TransportStatusCard.Visibility = Visibility.Collapsed;
        TransportConnectingRing.Visibility = Visibility.Collapsed;
        TransportConnectedBadge.Visibility = Visibility.Collapsed;
        ApplyStep(WizardStep.Protocol);
    }

    private void OnChooseCredentialsClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ApplyStep(WizardStep.Credentials);
    }

    private void OnBackToMethodClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _awaitingCode) return;
        CaptchaRow.Visibility = Visibility.Collapsed;
        ApplyStep(WizardStep.MethodChoice);
    }

    private async Task ConnectProtocolAndAdvanceAsync()
    {
        var kind = SelectedTransportKind;
        ReleaseTransportSessionAsync();

        if (kind == FoxyLoginTransportKind.None)
        {
            _accountService.SetTemporaryLoginProxy(null);
            _transportSession = new FoxyLoginTransportSession(
                FoxyLoginTransportKind.None,
                socksProxyEndpoint: null,
                displayLabel: "Direct");
            ApplyStep(WizardStep.MethodChoice);
            return;
        }

        CancelTransportAttempt();
        _transportCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var attemptCt = _transportCts.Token;

        SetTransportConnectingState(true, kind);
        try
        {
            var session = await _accountService.AcquireLoginTransportAsync(
                kind,
                msg => Dispatcher.BeginInvoke(() =>
                {
                    if (!attemptCt.IsCancellationRequested)
                        TransportStatusDetail.Text = msg;
                }),
                attemptCt);

            if (attemptCt.IsCancellationRequested)
            {
                _ = session.DisposeAsync();
                return;
            }

            _transportSession = session;
            _accountService.SetTemporaryLoginProxy(session.SocksProxyEndpoint);

            SetTransportConnectedState(session);
            await Task.Delay(550, attemptCt);

            if (!attemptCt.IsCancellationRequested)
            {
                SetBusy(false, "");
                ApplyStep(WizardStep.MethodChoice);
            }
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "");
            TransportStatusCard.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _accountService.SetTemporaryLoginProxy(null);
            SetBusy(false, "");
            TransportStatusCard.Visibility = Visibility.Collapsed;
            ShowError(ex.Message);
        }
        finally
        {
            TransportComboBox.IsEnabled = true;
        }
    }

    private void SetTransportConnectingState(bool connecting, FoxyLoginTransportKind kind)
    {
        TransportComboBox.IsEnabled = !connecting;
        SetBusy(connecting, "");
        if (!connecting) return;

        TransportStatusCard.Background = new SolidColorBrush(Color.FromArgb(0x15, 0x0E, 0xA5, 0xE9));
        TransportStatusCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, 0x0E, 0xA5, 0xE9));
        TransportConnectingRing.Visibility = Visibility.Visible;
        TransportConnectedBadge.Visibility = Visibility.Collapsed;
        TransportStatusTitle.Text = $"Connecting to {FormatKindName(kind)}...";
        TransportStatusTitle.Foreground = (Brush)FindResource("TextPrimaryBrush");
        TransportStatusDetail.Text = "Connecting...";
        TransportStatusCard.Visibility = Visibility.Visible;
    }

    private void SetTransportConnectedState(FoxyLoginTransportSession session)
    {
        TransportStatusCard.Background = new SolidColorBrush(Color.FromArgb(0x20, 0x10, 0xB9, 0x81));
        TransportStatusCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x10, 0xB9, 0x81));
        TransportConnectingRing.Visibility = Visibility.Collapsed;
        TransportConnectedBadge.Visibility = Visibility.Visible;
        TransportStatusTitle.Text = $"Connected — {session.DisplayLabel}";
        TransportStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
        TransportStatusDetail.Text = "Ready";
    }

    private static string FormatKindName(FoxyLoginTransportKind kind) => kind switch
    {
        FoxyLoginTransportKind.Lantern => "Lantern",
        FoxyLoginTransportKind.Veep => "Veep",
        FoxyLoginTransportKind.V2Ray => "V2Ray",
        FoxyLoginTransportKind.WireGuard => "Aether WireGuard",
        FoxyLoginTransportKind.Masque => "Aether MASQUE",
        _ => "Direct",
    };

    private void CancelTransportAttempt()
    {
        if (_transportCts is null) return;
        try { _transportCts.Cancel(); } catch { }
        try { _transportCts.Dispose(); } catch { }
        _transportCts = null;
    }

    private void ReleaseTransportSessionAsync()
    {
        _accountService.SetTemporaryLoginProxy(null);
        var session = Interlocked.Exchange(ref _transportSession, null);
        if (session is not null)
            _ = session.DisposeAsync().AsTask();
    }

    private async void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (_step == WizardStep.Protocol)
        {
            if (_busy) return;
            ErrorBorder.Visibility = Visibility.Collapsed;
            await ConnectProtocolAndAdvanceAsync();
            return;
        }

        if (_step == WizardStep.MethodChoice)
            return;

        if (_captchaTcs is { Task.IsCompleted: false } pendingCaptcha)
        {
            var answer = CaptchaInput.Text.Trim();
            if (answer.Length == 0)
            {
                ShowError("Enter the characters shown in the CAPTCHA image.");
                CaptchaInput.Focus();
                return;
            }
            CaptchaRow.Visibility = Visibility.Collapsed;
            SetBusy(true, "Verifying CAPTCHA...");
            pendingCaptcha.TrySetResult(answer);
            return;
        }

        if (_busy) return;
        ErrorBorder.Visibility = Visibility.Collapsed;

        if (!_awaitingCode)
        {
            var email = EmailInput.Text.Trim();
            var password = PasswordInput.Password;

            if (email.Length == 0 || !email.Contains('@'))
            {
                ShowError(Loc.Of("Enter the email address of your Mozilla account."));
                EmailInput.Focus();
                return;
            }
            if (password.Length == 0)
            {
                ShowError(Loc.Of("Enter your Mozilla account password."));
                PasswordInput.Focus();
                return;
            }

            _email = email;
            SetBusy(true, Loc.Of("Signing in..."));
            FxaLoginResult result;
            try
            {
                result = await _accountService.StartLoginAsync(email, password, _cts.Token, PromptCaptchaAsync);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                ShowError(ex.Message);
                return;
            }

            if (_cts.IsCancellationRequested) return;

            if (result.Requires2Fa && !string.IsNullOrEmpty(result.SessionToken))
            {
                _sessionToken = result.SessionToken;
                _awaitingCode = true;
                SetBusy(false, "");
                ShowCodeStep();
                return;
            }

            if (result.Success && result.Account is not null)
            {
                Result = _accountService.StoreAccount(result.Account);
                DialogResult = true;
                return;
            }

            SetBusy(false, "");
            RestoreInputsAfterAttempt();
            ShowError(result.ErrorMessage ?? Loc.Of("Firefox Accounts rejected this sign-in."));
            return;
        }

        var code = CodeInput.Text.Trim();
        if (code.Length < 4)
        {
            ShowError(Loc.Of("Enter the verification code Mozilla emailed you."));
            CodeInput.Focus();
            return;
        }

        SetBusy(true, Loc.Of("Checking the code..."));
        FxaLoginResult verified;
        try
        {
            verified = await _accountService.SubmitVerificationCodeAsync(_email, _sessionToken, code, _cts.Token, PromptCaptchaAsync);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetBusy(false, "");
            ShowError(ex.Message);
            return;
        }

        if (_cts.IsCancellationRequested) return;

        if (verified.Success && verified.Account is not null)
        {
            Result = _accountService.StoreAccount(verified.Account);
            DialogResult = true;
            return;
        }

        SetBusy(false, "");
        RestoreInputsAfterAttempt();
        ShowError(verified.ErrorMessage ?? Loc.Of("That code did not work. Check the email and try again."));
    }

    private void RestoreInputsAfterAttempt()
    {
        CaptchaRow.Visibility = Visibility.Collapsed;
        if (_awaitingCode)
        {
            EmailRow.Visibility = Visibility.Collapsed;
            PasswordRow.Visibility = Visibility.Collapsed;
            CodeRow.Visibility = Visibility.Visible;
            BackToMethodButton.Visibility = Visibility.Collapsed;
            CodeInput.IsEnabled = true;
            return;
        }

        EmailRow.Visibility = Visibility.Visible;
        PasswordRow.Visibility = Visibility.Visible;
        CodeRow.Visibility = Visibility.Collapsed;
        BackToMethodButton.Visibility = Visibility.Visible;
        EmailInput.IsEnabled = true;
        PasswordInput.IsEnabled = true;
    }

    private void ShowCodeStep()
    {
        CodeRow.Visibility = Visibility.Visible;
        PasswordInput.IsEnabled = false;
        EmailInput.IsEnabled = false;
        BackToMethodButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = Loc.Of("Verify");
        EmailRow.Visibility = Visibility.Collapsed;
        PasswordRow.Visibility = Visibility.Collapsed;
        CaptchaRow.Visibility = Visibility.Collapsed;
        CodeInput.Focus();
    }

    private async Task<string> PromptCaptchaAsync(string imageDataUrl, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await Dispatcher.InvokeAsync(() =>
        {
            _captchaTcs = tcs;
            CaptchaImage.Source = DecodeDataUrlImage(imageDataUrl);
            CaptchaInput.Text = "";
            BackToMethodButton.Visibility = Visibility.Collapsed;
            EmailRow.Visibility = Visibility.Collapsed;
            PasswordRow.Visibility = Visibility.Collapsed;
            CodeRow.Visibility = Visibility.Collapsed;
            CaptchaRow.Visibility = Visibility.Visible;
            ErrorBorder.Visibility = Visibility.Collapsed;
            BusyIndicator.Visibility = Visibility.Collapsed;
            BusyText.Text = "";
            _busy = false;
            PrimaryButton.IsEnabled = true;
            EmailInput.IsEnabled = false;
            PasswordInput.IsEnabled = false;
            CodeInput.IsEnabled = false;
            CaptchaInput.IsEnabled = true;
            CaptchaInput.Focus();
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _captchaTcs = null;
        }
    }

    private static BitmapImage DecodeDataUrlImage(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
            throw new InvalidOperationException("Invalid CAPTCHA image data URL.");

        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        PrimaryButton.IsEnabled = !busy;
        BrowserSignInButton.IsEnabled = !busy;
        DirectSignInButton.IsEnabled = !busy;
        BackToMethodButton.IsEnabled = !busy;
        EmailInput.IsEnabled = !busy && !_awaitingCode;
        PasswordInput.IsEnabled = !busy && !_awaitingCode;
        CodeInput.IsEnabled = !busy;
    }

    private void OnBrowserSignInClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ErrorBorder.Visibility = Visibility.Collapsed;

        var browserWin = new FoxyBrowserLoginWindow(
            _accountService.GetBrowserProxyServerArg(),
            EmailInput.Text.Trim(),
            (email, sessionToken, ct) => _accountService.CompleteBrowserLoginAsync(email, sessionToken, ct))
        {
            Owner = this
        };
        if (browserWin.ShowDialog() != true || browserWin.AccountResult is not { } account)
            return;

        Result = _accountService.StoreAccount(account);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        OnPrimaryClicked(sender, e);
        e.Handled = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CancelTransportAttempt();
        DialogResult = false;
        Close();
    }

    private void OnDialogBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }
}
