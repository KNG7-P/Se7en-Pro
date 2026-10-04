using Se7enPro.Services;

namespace Se7enPro.ViewModels;

/// <summary>
/// Localized UI strings for the custom DNS settings card.
/// </summary>
public sealed class DnsLabels
{
    public string CardTitle => Loc.Of("Custom DNS");

    public string CardSubtitle => Loc.Of(
        "Resolvers the tunnel answers DNS from. Blank means automatic: the built-in public resolvers are used. "
        + "Changes save themselves.");

    public string UdpLabel => Loc.Of("Plain DNS (UDP)");
    public string UdpHint => Loc.Of("Bare addresses or hosts, port 53.");
    public string UdpPlaceholder => Loc.Of("9.9.9.9, 149.112.112.112");

    public string DotLabel => Loc.Of("DNS over TLS (DoT)");
    public string DotHint => Loc.Of("Each entry needs the tls:// prefix, port 853 by default.");
    public string DotPlaceholder => Loc.Of("tls://dns.google, tls://dns.quad9.net");

    public string DohLabel => Loc.Of("DNS over HTTPS (DoH)");
    public string DohHint => Loc.Of(
        "Full https:// URLs, or a host with the doh: prefix. The lookup rides an ordinary "
        + "HTTPS request, so it is the hardest to block.");
    public string DohPlaceholder => Loc.Of("https://cloudflare-dns.com/dns-query, doh:dns.quad9.net");

    public string Automatic => Loc.Of("Automatic resolvers");
    public string ResetLabel => Loc.Of("Clear list");
    public string TestLabel => Loc.Of("Test");

    public string StrictLabel => Loc.Of("Only use my resolvers");
    public string StrictLabelOn => Loc.Of("Only my resolvers");

    public string StrictHint => Loc.Of(
        "Never fall back to the built-in defaults. Leave this off so a resolver that goes "
        + "down does not take all DNS with it.");

    /// <summary>Shown briefly after an automatic save.</summary>
    public string AutoSaved => Loc.Of("Saved");

    public string Testing => Loc.Of("Testing…");
    public string EnterOneFirst => Loc.Of("Enter at least one address first");
    public string NoneAnswered => Loc.Of(
        "No resolver answered. One that does not answer here will not answer through the tunnel either.");

    /// <summary>Warning when strict mode is active without plain UDP resolvers in TUN mode.</summary>
    public string TunnelWarning => Loc.Of(
        "Strict mode with no plain-UDP entry: in TUN mode the local forwarder can only dial "
        + "plain DNS, so apps that resolve through the tunnel adapter will get no answer. "
        + "Add a plain-UDP entry, or turn strict mode off.");
}