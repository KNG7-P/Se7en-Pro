using System;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Se7enPro.Services;

namespace Se7enPro.Views;

public partial class AddSiteDialog : Window
{
    public string ResultKind { get; private set; } = "domain";
    public string ResultValue { get; private set; } = "";

    public AddSiteDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            TargetInput.Focus();
            if (Owner is MainWindow mw) mw.ShowBackdrop();
            else if (Application.Current?.MainWindow is MainWindow main) main.ShowBackdrop();
        };
        Closed += (_, _) =>
        {
            if (Owner is MainWindow mw) mw.HideBackdrop();
            else if (Application.Current?.MainWindow is MainWindow main) main.HideBackdrop();
        };
    }

    private void OnRuleTypeChanged(object sender, RoutedEventArgs e)
    {
        if (FieldLabel is null || TargetInput is null || SubdomainsCheckBox is null || DnsRow is null)
            return;

        var isDomain = DomainRadio?.IsChecked == true;
        if (isDomain)
        {
            FieldLabel.Text = Loc.Of("DOMAIN OR HOSTNAME");
            TargetInput.ToolTip = Loc.Of("e.g. example.com or api.github.com");
            MaterialDesignThemes.Wpf.HintAssist.SetHint(TargetInput, Loc.Of("e.g. example.com or api.github.com"));
            SubdomainsCheckBox.Visibility = Visibility.Visible;
            DnsRow.Visibility = Visibility.Visible;
        }
        else
        {
            FieldLabel.Text = Loc.Of("IP ADDRESS OR CIDR SUBNET");
            TargetInput.ToolTip = Loc.Of("e.g. 192.168.1.0/24 or 1.1.1.1");
            MaterialDesignThemes.Wpf.HintAssist.SetHint(TargetInput, Loc.Of("e.g. 192.168.1.0/24 or 1.1.1.1"));
            SubdomainsCheckBox.Visibility = Visibility.Collapsed;
            DnsRow.Visibility = Visibility.Collapsed;
        }

        ErrorBorder.Visibility = Visibility.Collapsed;
        ResolvedIpBorder.Visibility = Visibility.Collapsed;
    }

    private void OnInputTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
        ResolvedIpBorder.Visibility = Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            OnAddClicked(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            OnCancelClicked(sender, e);
        }
    }

    private async void OnResolveDnsClicked(object sender, RoutedEventArgs e)
    {
        var raw = TargetInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowError(Loc.Of("Please enter a domain name first."));
            return;
        }

        var normalized = SplitRules.NormalizeSplitDomain(raw);
        if (string.IsNullOrEmpty(normalized))
        {
            ShowError(Loc.Of("Please enter a valid domain name."));
            return;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(normalized);
            if (addresses.Length > 0)
            {
                ResolvedIpText.Text = addresses[0].ToString();
                ResolvedIpBorder.Visibility = Visibility.Visible;
                ErrorBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowError(string.Format(Loc.Of("Could not resolve {0}"), normalized));
            }
        }
        catch (Exception ex)
        {
            ShowError(string.Format(Loc.Of("DNS resolve failed: {0}"), ex.Message));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var raw = TargetInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowError(Loc.Of("Please enter a target address."));
            return;
        }

        var isDomain = DomainRadio.IsChecked == true;
        if (isDomain)
        {
            var normalized = SplitRules.NormalizeSplitDomain(raw);
            if (string.IsNullOrEmpty(normalized))
            {
                ShowError(Loc.Of("Please enter a valid domain (e.g. example.com)."));
                return;
            }

            if (SubdomainsCheckBox.IsChecked == true && !normalized.StartsWith("*."))
            {
                normalized = "*." + normalized;
            }

            ResultKind = "domain";
            ResultValue = normalized;
        }
        else
        {
            var normalized = SplitRules.NormalizeSplitIpCidr(raw);
            if (string.IsNullOrEmpty(normalized))
            {
                ShowError(Loc.Of("Please enter a valid IP address or CIDR range (e.g. 192.168.1.0/24 or 1.1.1.1)."));
                return;
            }

            ResultKind = "ip";
            ResultValue = normalized;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }
}
