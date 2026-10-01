using System;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed partial class AboutViewModel : PageViewModelBase
{
    public override string Title => "About";
    public override string Route => "about";
    public override string Icon => "InformationOutline";

    public string AppName => "Se7en Pro";
    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.5";
    public string Copyright => "Built on Psiphon 3 (GPLv3). Modern UI by Se7en Pro.";

    private static string PsiphonWebPage(string page) =>
        $"https://s3.amazonaws.com/psiphon/web/{ChannelSegment()}/{page}";

    private static string ChannelSegment()
    {
        var id = EmbeddedValues.PropagationChannelId;
        if (string.IsNullOrWhiteSpace(id) || id.Equals("PROPAGATION_CHANNEL_ID", StringComparison.Ordinal))
        {
            return "psiphon-3";
        }
        return id;
    }

    [RelayCommand]
    private static void OpenInfoLink() =>
        OpenUrl("https://psiphon.ca/");

    [RelayCommand]
    private static void OpenFaq() =>
        OpenUrl(PsiphonWebPage("faq.html"));

    [RelayCommand]
    private static void OpenPrivacy() =>
        OpenUrl(PsiphonWebPage("privacy.html#information-collected"));

    [RelayCommand]
    private static void OpenGitHub() =>
        OpenUrl("https://github.com/KNG7-P/Se7en-Pro");

    [RelayCommand]
    private static void OpenTelegram() =>
        OpenUrl("https://t.me/King_network7");

    [RelayCommand]
    private static void OpenPsiphonCoreGitHub() =>
        OpenUrl("https://github.com/Psiphon-Labs/psiphon-tunnel-core");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {

        }
    }
}
