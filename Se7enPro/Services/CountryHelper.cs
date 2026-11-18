using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Se7enPro.Services;

public static partial class CountryHelper
{
    private static readonly string[] SeedRegionCodes =
    {
        "AT", "BE", "BG", "CA", "CH", "CZ", "DE", "DK", "ES", "FI", "FR",
        "GB", "HR", "HU", "IE", "IN", "IT", "JP", "LV", "MX", "NL", "NO",
        "PL", "PT", "RO", "RS", "SE", "SG", "SK", "UA", "US",
    };

    public static readonly IReadOnlySet<string> TorExitCountryCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AT", "AU", "BE", "BG", "BR", "CA", "CH", "CZ", "DE", "DK", "ES", "FI",
            "FR", "GB", "GR", "HR", "HU", "IE", "IN", "IT", "JP", "LT", "LU", "LV",
            "MD", "MX", "NL", "NO", "NZ", "PL", "PT", "RO", "RS", "RU", "SE", "SG",
            "SK", "UA", "US",
        };

    public static bool HasTorExit(string? code) =>
        !string.IsNullOrWhiteSpace(code) && TorExitCountryCodes.Contains(code.Trim());

    private static readonly Lazy<ObservableCollection<Country>> _seedRegions = new(() =>
    {
        var list = new ObservableCollection<Country>
        {
            new("auto", "Auto"),
        };
        var ordered = new List<Country>();
        foreach (var c in SeedRegionCodes) ordered.Add(new Country(c, FullName(c)));
        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in ordered) list.Add(entry);
        return list;
    });

        public static ObservableCollection<Country> BuildSeedRegions() => new(_seedRegions.Value);

    private static readonly Lazy<ObservableCollection<Country>> _torExitRegions = new(() =>
    {
        var list = new ObservableCollection<Country>
        {
            new("auto", "Auto"),
        };
        var ordered = new List<Country>();
        foreach (var code in TorExitCountryCodes)
        {
            if (HasCountryName(code)) ordered.Add(new Country(code, FullName(code)));
        }
        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in ordered) list.Add(entry);
        return list;
    });

    public static ObservableCollection<Country> BuildTorExitRegions() => new(_torExitRegions.Value);

    private static readonly Lazy<ObservableCollection<Country>> _foxyRegions = new(() =>
    {
        var list = new ObservableCollection<Country>
        {
            new("auto", "Auto"),
        };
        var ordered = new List<Country>();
        foreach (var country in Foxy.GuardianClient.GetBuiltInFallbackCountries())
        {
            if (string.IsNullOrEmpty(country.Code)
                || string.Equals(country.Code, "REC", StringComparison.OrdinalIgnoreCase)) continue;
            if (ordered.Any(c => string.Equals(c.Code, country.Code, StringComparison.OrdinalIgnoreCase))) continue;
            ordered.Add(new Country(country.Code.ToUpperInvariant(), country.Name));
        }
        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in ordered) list.Add(entry);
        return list;
    });

    public static ObservableCollection<Country> BuildFoxyRegions() => new(_foxyRegions.Value);

    private static readonly Lazy<ObservableCollection<Country>> _lanternRegions = new(() =>
    {
        var list = new ObservableCollection<Country>
        {
            new("auto", "Auto"),
        };
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DE", "NL"
        };
        var ordered = new List<Country>();
        foreach (var code in codes)
        {
            if (HasCountryName(code)) ordered.Add(new Country(code, FullName(code)));
        }
        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in ordered) list.Add(entry);
        return list;
    });

    public static ObservableCollection<Country> BuildLanternRegions(IEnumerable<string>? dynamicCodes = null)
    {
        return new ObservableCollection<Country>(_lanternRegions.Value);
    }

    private static readonly Lazy<ObservableCollection<Country>> _veepRegions = new(() =>
    {
        var list = new ObservableCollection<Country>
        {
            new("auto", "Auto"),
            new("fr-prs", "France (Paris)"),
            new("nl", "Netherlands (Amsterdam)"),
            new("gb-lnd", "United Kingdom (London)"),
            new("us-va", "United States (Virginia)"),
            new("us-or", "United States (Oregon)"),
            new("sg", "Singapore"),
            new("ru-spb", "Russia (Saint Petersburg)"),
        };
        return list;
    });

    public static ObservableCollection<Country> BuildVeepRegions() => new(_veepRegions.Value);
}

public sealed record Country(string Code, string EnglishName) : LocalizedItem
{
    public string Name => Loc.Of(EnglishName);

    public bool Equals(Country? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Code ?? string.Empty);
}
