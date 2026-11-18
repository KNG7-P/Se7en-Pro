using System;
using System.Collections.Generic;

namespace Se7enPro.Services;

public static partial class CountryHelper
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
        var clean = code.Trim();
        if (Names.TryGetValue(clean, out var direct)) return direct;
        var dash = clean.IndexOf('-');
        var iso = dash > 0 ? clean[..dash] : clean;
        return Names.TryGetValue(iso.ToUpperInvariant(), out var n) ? n : clean.ToUpperInvariant();
    }

    internal static bool HasCountryName(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Names.ContainsKey(code.Trim());

    public static bool HasFlag(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var clean = code.Trim();
        var dash = clean.IndexOf('-');
        var iso = dash > 0 ? clean[..dash] : clean;
        return iso.Length == 2 || Names.ContainsKey(iso.ToUpperInvariant());
    }

    private static readonly HashSet<string> KnownIsoCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AD","AE","AF","AG","AI","AL","AM","AO","AR","AT","AU","AW","AZ",
        "BA","BB","BD","BE","BF","BG","BH","BI","BJ","BM","BN","BO","BR","BS","BT","BW","BY","BZ",
        "CA","CD","CF","CG","CH","CI","CL","CM","CN","CO","CR","CU","CV","CY","CZ",
        "DE","DJ","DK","DM","DO","DZ","EC","EE","EG","ER","ES","ET",
        "FI","FJ","FM","FO","FR","GA","GB","GD","GE","GH","GI","GL","GM","GN","GQ","GR","GT","GW","GY",
        "HK","HN","HR","HT","HU","ID","IE","IL","IN","IQ","IR","IS","IT",
        "JM","JO","JP","KE","KG","KH","KM","KN","KP","KR","KW","KY","KZ",
        "LA","LB","LC","LI","LK","LR","LS","LT","LU","LV","LY",
        "MA","MC","MD","ME","MG","MK","ML","MM","MN","MO","MR","MT","MU","MV","MW","MX","MY","MZ",
        "NA","NE","NG","NI","NL","NO","NP","NZ","OM","PA","PE","PG","PH","PK","PL","PR","PS","PT","PY",
        "QA","RO","RS","RU","RW","SA","SB","SC","SD","SE","SG","SI","SK","SL","SM","SN","SO","SR","ST","SV","SY","SZ",
        "TD","TG","TH","TJ","TL","TM","TN","TO","TR","TT","TV","TW","TZ",
        "UA","UG","US","UY","UZ","VA","VC","VE","VN","VU","WS","XK","YE","ZA","ZM","ZW"
    };

    private static readonly (string Name, string Code)[] CountryNameTokens = BuildCountryNameTokens();

    private static (string Name, string Code)[] BuildCountryNameTokens()
    {
        var list = new List<(string, string)>(Names.Count + 10);
        foreach (var kv in Names)
        {
            list.Add((kv.Value, kv.Key));
        }
        list.Add(("Turkey", "TR"));
        list.Add(("Turkiye", "TR"));
        list.Add(("USA", "US"));
        list.Add(("America", "US"));
        list.Add(("Britain", "GB"));
        list.Add(("England", "GB"));
        list.Add(("Korea", "KR"));
        list.Add(("UAE", "AE"));
        list.Add(("Czech", "CZ"));
        list.Add(("Holland", "NL"));
        list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return list.ToArray();
    }

    public static string ExtractCountryCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]) &&
                char.IsHighSurrogate(text[i + 2]) && char.IsLowSurrogate(text[i + 3]))
            {
                var cp1 = char.ConvertToUtf32(text[i], text[i + 1]);
                var cp2 = char.ConvertToUtf32(text[i + 2], text[i + 3]);
                if (cp1 is >= 0x1F1E6 and <= 0x1F1FF && cp2 is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    var code = new string(new[]
                    {
                        (char)('A' + (cp1 - 0x1F1E6)),
                        (char)('A' + (cp2 - 0x1F1E6))
                    });
                    if (code == "UK") code = "GB";
                    if (KnownIsoCodes.Contains(code)) return code;
                }
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!IsAsciiLetter(text[i])) continue;
            var start = i;
            while (i < text.Length && IsAsciiLetter(text[i])) i++;
            var len = i - start;
            if (len != 2) continue;

            var prev = start > 0 ? text[start - 1] : '\0';
            var next = i < text.Length ? text[i] : '\0';
            var prevValid = prev == '\0' || (!char.IsLetterOrDigit(prev) && prev != '.');
            var nextValid = next == '\0' || !char.IsLetter(next);
            if (!prevValid || !nextValid) continue;

            var c1 = text[start];
            var c2 = text[start + 1];
            var isUpper = char.IsUpper(c1) && char.IsUpper(c2);
            var isBracketed = (prev == '[' && next == ']') || (prev == '(' && next == ')') || prev == '#';
            if (!isUpper && !isBracketed) continue;

            var token = text.Substring(start, 2).ToUpperInvariant();
            if (token == "UK") return "GB";
            if (isBracketed ? KnownIsoCodes.Contains(token) : Names.ContainsKey(token))
                return token;
        }

        foreach (var (name, code) in CountryNameTokens)
        {
            var idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var beforeOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                var end = idx + name.Length;
                var afterOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
                if (beforeOk && afterOk) return code;
                idx = text.IndexOf(name, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return "";
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
