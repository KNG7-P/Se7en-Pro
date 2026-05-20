using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PsiphonUI.Services;

public static class CountryHelper
{

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AE"] = "United Arab Emirates",
        ["AR"] = "Argentina",
        ["AT"] = "Austria",
        ["AU"] = "Australia",
        ["BE"] = "Belgium",
        ["BG"] = "Bulgaria",
        ["BR"] = "Brazil",
        ["CA"] = "Canada",
        ["CH"] = "Switzerland",
        ["CL"] = "Chile",
        ["CN"] = "China",
        ["CO"] = "Colombia",
        ["CY"] = "Cyprus",
        ["CZ"] = "Czechia",
        ["DE"] = "Germany",
        ["DK"] = "Denmark",
        ["EE"] = "Estonia",
        ["EG"] = "Egypt",
        ["ES"] = "Spain",
        ["FI"] = "Finland",
        ["FR"] = "France",
        ["GB"] = "United Kingdom",
        ["GR"] = "Greece",
        ["HK"] = "Hong Kong",
        ["HR"] = "Croatia",
        ["HU"] = "Hungary",
        ["ID"] = "Indonesia",
        ["IE"] = "Ireland",
        ["IL"] = "Israel",
        ["IN"] = "India",
        ["IS"] = "Iceland",
        ["IT"] = "Italy",
        ["JP"] = "Japan",
        ["KR"] = "South Korea",
        ["LT"] = "Lithuania",
        ["LU"] = "Luxembourg",
        ["LV"] = "Latvia",
        ["MD"] = "Moldova",
        ["MX"] = "Mexico",
        ["MY"] = "Malaysia",
        ["NL"] = "Netherlands",
        ["NO"] = "Norway",
        ["NZ"] = "New Zealand",
        ["PH"] = "Philippines",
        ["PL"] = "Poland",
        ["PT"] = "Portugal",
        ["RO"] = "Romania",
        ["RS"] = "Serbia",
        ["RU"] = "Russia",
        ["SE"] = "Sweden",
        ["SG"] = "Singapore",
        ["SI"] = "Slovenia",
        ["SK"] = "Slovakia",
        ["TH"] = "Thailand",
        ["TR"] = "Türkiye",
        ["TW"] = "Taiwan",
        ["UA"] = "Ukraine",
        ["US"] = "United States",
        ["VN"] = "Vietnam",
        ["ZA"] = "South Africa",
    };

    public static string FullName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        return Names.TryGetValue(code, out var n) ? n : code.ToUpperInvariant();
    }

    public static bool HasFlag(string? code) =>
    !string.IsNullOrWhiteSpace(code) && Names.ContainsKey(code);

    private static readonly string[] SeedRegionCodes =
    {
        "AT", "BE", "BG", "CA", "CH", "CZ", "DE", "DK", "ES", "FI", "FR",
        "GB", "HR", "HU", "IE", "IN", "IT", "JP", "LV", "MX", "NL", "NO",
        "PL", "PT", "RO", "RS", "SE", "SG", "SK", "UA", "US",
    };

    public static ObservableCollection<Country> BuildSeedRegions()
    {
        var list = new ObservableCollection<Country>
        {
            new("auto", "Auto (best available)"),
        };
        var ordered = new List<Country>();
        foreach (var c in SeedRegionCodes) ordered.Add(new Country(c, FullName(c)));
        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in ordered) list.Add(entry);
        return list;
    }
}

public sealed record Country(string Code, string Name);
