using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;

namespace PsiphonUI.ViewModels;

public sealed partial class AboutViewModel : PageViewModelBase
{
    public override string Title => "About";
    public override string Route => "about";
    public override string Icon => "InformationOutline";

    public string AppName => "PsiphonUI";
    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    public string Copyright => "Built on Psiphon 3 (GPLv3). Modern UI by PsiphonUI.";

    [RelayCommand]
    private static void OpenInfoLink() =>
        OpenUrl("https://psiphon.ca/");

    [RelayCommand]
    private static void OpenFaq() =>
        OpenUrl("https://psiphon.ca/faq.html");

    [RelayCommand]
    private static void OpenPrivacy() =>
        OpenUrl("https://psiphon.ca/en/privacy.html");

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
