using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PsiphonUI.Services;

public static class IpScannerPresets
{
    public sealed record LabeledRange(string Cidr, string Label, bool DefaultSelected = true);

    public sealed record Preset(
    string Id,
    string Name,
    IReadOnlyList<string> Snis,
    IReadOnlyList<LabeledRange> Ranges);

    private static readonly string[] AkamaiSnis =
    {
        "a248.e.akamai.net",
        "a77.net.akamai.net",
        "a104.net.akamai.net",
        "a184.net.akamai.net",
        "ds-aksb.akamaized.net",
        "ak.net.akamaized.net",
    };

    private static readonly LabeledRange[] AkamaiBaselineRanges =
{
        new("2.16.0.0/24",   "EU"),
        new("2.17.0.0/24",   "EU"),
        new("2.18.0.0/24",   "EU"),
        new("2.19.0.0/24",   "EU"),
        new("2.20.0.0/24",   "EU"),
        new("2.21.0.0/24",   "EU"),
        new("2.22.0.0/24",   "EU"),
        new("23.32.0.0/24",  "US"),
        new("23.48.0.0/24",  "US"),
        new("23.58.0.0/24",  "US"),
        new("23.72.0.0/24",  "US"),
        new("23.192.0.0/24", "US"),
        new("23.193.0.0/24", "US"),
        new("23.202.0.0/24", "US"),
        new("23.43.0.0/24",  "US"),
        new("104.64.0.0/24", "Global"),
        new("104.65.0.0/24", "Global"),
        new("104.103.0.0/24","Global"),
        new("104.112.0.0/24","Global"),
        new("184.24.0.0/24", "Global"),
        new("184.84.0.0/24", "Global"),
        new("184.86.0.0/24", "Global"),
        new("185.200.232.0/24","EU"),
        new("72.246.0.0/24", "US"),
        new("92.16.0.0/24",  "EU"),
        new("92.122.0.0/24", "EU"),
    };

    private static readonly LabeledRange[] AkamaiRanges = BuildAkamaiRanges();

    private static LabeledRange[] BuildAkamaiRanges()
    {
        var ranges = new List<LabeledRange>(AkamaiBaselineRanges);
        var seen = new HashSet<string>(ranges.Select(r => r.Cidr), StringComparer.Ordinal);
        AppendSeededAkamaiRanges(ranges, seen);
        return ranges.ToArray();
    }

    private static void AppendSeededAkamaiRanges(List<LabeledRange> ranges, HashSet<string> seen)
    {
        var asm = typeof(IpScannerPresets).Assembly;

        const string resName = "PsiphonUI.Resources.akamai_seed_ips.txt";
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null) return;

        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] == '#') continue;
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            var parts = trimmed.Split('.');
            if (parts.Length != 4) continue;
            if (!byte.TryParse(parts[0], out var a)) continue;
            if (!byte.TryParse(parts[1], out var b)) continue;
            if (!byte.TryParse(parts[2], out var c)) continue;

            if (!byte.TryParse(parts[3], out _)) continue;

            var cidr = $"{a}.{b}.{c}.0/24";
            if (seen.Add(cidr))
            {

                ranges.Add(new LabeledRange(cidr, ClassifyAkamaiRegion(a, b), DefaultSelected: false));
            }
        }
    }

    private static string ClassifyAkamaiRegion(byte a, byte b)
    {

        if (a == 2 && b >= 16 && b <= 23) return "EU";

        if (a == 23) return "US";

        if (a == 72) return "US";

        if (a == 88 && b == 221) return "EU";
        if (a == 92 && (b == 16 || b == 122 || b == 123)) return "EU";
        if (a == 95 && (b == 100 || b == 101)) return "EU";
        if (a == 96 && (b == 16 || b == 17)) return "US";

        if (a == 104) return "Global";

        if (a == 173 && (b == 222 || b == 223)) return "Global";

        if (a == 184) return "Global";
        return "Global";
    }

    private static readonly string[] GoogleSnis =
    {
        "fonts.googleapis.com",
        "ajax.googleapis.com",
        "storage.googleapis.com",
        "www.gstatic.com",
        "ssl.gstatic.com",
        "accounts.google.com",
    };

    private static readonly LabeledRange[] GoogleRanges =
    {
        new("34.143.0.0/24",   "Cloud Run"),
        new("34.160.0.0/24",   "Cloud"),
        new("34.96.0.0/24",    "Cloud"),
        new("35.186.0.0/24",   "Cloud"),
        new("64.233.160.0/24", "Core"),
        new("66.249.80.0/24",  "Core"),
        new("74.125.0.0/24",   "Core"),
        new("142.250.0.0/24",  "Core"),
        new("172.217.0.0/24",  "Core"),
        new("216.58.192.0/24", "Core"),
        new("35.201.0.0/24",   "Cloud"),
        new("34.117.0.0/24",   "Cloud"),
    };

    private static readonly string[] AmazonSnis =
    {
        "d1.cloudfront.net",
        "d2.cloudfront.net",
        "d3.cloudfront.net",
        "aws.cloudfront.net",
        "s3.amazonaws.com",
        "edge.cloudfront.net",
    };

    private static readonly LabeledRange[] AmazonRanges =
    {
        new("13.32.0.0/24",     "US"),
        new("13.35.0.0/24",     "US"),
        new("52.46.0.0/24",     "US"),
        new("54.192.0.0/24",    "Global"),
        new("54.230.0.0/24",    "Global"),
        new("99.84.0.0/24",     "Global"),
        new("130.176.0.0/24",   "Global"),
        new("143.204.0.0/24",   "Global"),
        new("205.251.192.0/24", "Global"),
        new("54.239.128.0/24",  "Global"),
    };

    private static readonly string[] AzureSnis =
    {
        "ajax.aspnetcdn.com",
        "az416426.vo.msecnd.net",
        "az784690.vo.msecnd.net",
        "cdn.office.net",
        "static.azureedge.net",
        "az.msecnd.net",
    };

    private static readonly LabeledRange[] AzureRanges =
    {
        new("13.107.4.0/24",  "Core"),
        new("23.96.0.0/24",   "US"),
        new("40.64.0.0/24",   "Global"),
        new("52.224.0.0/24",  "US"),
        new("104.208.0.0/24", "Global"),
        new("137.116.0.0/24", "Global"),
        new("168.61.0.0/24",  "US"),
    };

    private static readonly LabeledRange[] IranIspMixRanges =
    {
        new("184.24.77.42",    "MCI"),
        new("184.24.77.32",    "MCI"),
        new("185.200.232.49",  "MCI"),
        new("23.48.23.151",    "MCI"),
        new("104.112.146.82",  "MCI"),
        new("184.24.77.7",     "MCI"),
        new("2.22.250.149",    "Irancell"),
        new("23.58.193.140",   "Irancell"),
        new("184.24.77.5",     "Irancell"),
        new("185.200.232.50",  "Irancell"),
        new("23.43.237.239",   "Irancell"),
        new("92.16.53.11",     "Irancell"),
        new("184.24.77.21",    "Rightel"),
        new("185.200.232.42",  "Rightel"),
        new("23.48.23.186",    "Rightel"),
        new("72.246.28.3",     "Rightel"),
        new("92.122.0.1",      "Rightel"),
        new("184.24.77.11",    "Shatel"),
        new("185.200.232.41",  "Shatel"),
        new("23.48.23.133",    "Shatel"),
        new("2.19.126.81",     "Shatel"),
        new("104.64.0.5",      "Shatel"),
    };

    public static readonly IReadOnlyList<Preset> All = new Preset[]
{
        new(Id: "akamai",  Name: "Akamai",            Snis: AkamaiSnis, Ranges: AkamaiRanges),
        new(Id: "google",  Name: "Google CDN",        Snis: GoogleSnis, Ranges: GoogleRanges),
        new(Id: "amazon",  Name: "Amazon CloudFront", Snis: AmazonSnis, Ranges: AmazonRanges),
        new(Id: "azure",   Name: "Microsoft Azure",   Snis: AzureSnis,  Ranges: AzureRanges),
        new(Id: "iran-isp",Name: "Iran ISP pre-tested (MCI / Irancell / Rightel / Shatel)",
                                                     Snis: AkamaiSnis, Ranges: IranIspMixRanges),
};
}
