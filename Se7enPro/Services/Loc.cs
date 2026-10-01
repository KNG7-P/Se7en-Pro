using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace Se7enPro.Services;

public static class Loc
{
    public const string DefaultLanguage = "en";

    private const string Prefix = "Loc_";

    private static bool _culturePinned;

    private static Dictionary<string, string>? _table;

        private static Dictionary<string, string>? _bySource;

        public static string Language { get; private set; } = DefaultLanguage;

        public static event Action? Changed;

    public static string T(string key) => T(key, key);

        public static string T(string key, string fallback)
    {
        var table = Volatile.Read(ref _table) ?? EnsureTables(Language);
        return table is not null && table.TryGetValue(key, out var value) ? value : fallback;
    }

        public static string Of(string english)
    {
        if (english.Length == 0 || Language == DefaultLanguage) return english;
        var map = Volatile.Read(ref _bySource) ?? EnsureTables(Language);
        return map is not null && map.TryGetValue(english, out var value) ? value : english;
    }

        private static Dictionary<string, string>? EnsureTables(string lang)
    {
        try
        {
            PublishTables(lang);
        }
        catch
        {
            return null;
        }

        return Volatile.Read(ref _table);
    }

        private static Dictionary<string, string> BuildSourceMap(
        Dictionary<string, string> english, Dictionary<string, string> active)
    {
        var map = new Dictionary<string, string>(english.Count, StringComparer.Ordinal);
        foreach (var entry in english)
        {
            if (entry.Value.Length == 0) continue;
            if (!active.TryGetValue(entry.Key, out var text)) continue;
            if (string.Equals(entry.Value, text, StringComparison.Ordinal)) continue;
            map.TryAdd(entry.Value, text);
        }

        return map;
    }

        public static void Apply(string? language)
    {
        var lang = Normalize(language);
        if (lang == Language && Volatile.Read(ref _table) is not null)
        {
            ApplyCulture(lang);
            return;
        }

        Language = lang;
        ApplyCulture(lang);
        SwapDictionary(lang);
        Changed?.Invoke();
    }

    private static string Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "ru" => "ru",
        "zh" => "zh",
        _ => DefaultLanguage,
    };

    private static void SwapDictionary(string lang)
    {
        ResourceDictionary next;
        try
        {
            next = PublishTables(lang);
        }
        catch
        {
            
            
            return;
        }

        var app = Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;

        
        
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString;
            if (source is null) continue;
            if (source.EndsWith("Strings.en.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Strings.ru.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Strings.zh.xaml", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index >= 0) merged[index] = next;
        else merged.Add(next);
    }

        private static ResourceDictionary PublishTables(string lang)
    {
        var english = Load(DefaultLanguage);
        var active = lang == DefaultLanguage ? english : Load(lang);
        var englishTable = BuildTable(english);
        var activeTable = ReferenceEquals(active, english) ? englishTable : BuildTable(active);

        Volatile.Write(ref _table, activeTable);
        Volatile.Write(ref _bySource, BuildSourceMap(englishTable, activeTable));
        return active;
    }

    private static ResourceDictionary Load(string lang) => new()
    {
        Source = new Uri($"pack://application:,,,/Resources/Strings.{lang}.xaml", UriKind.Absolute),
    };

    private static Dictionary<string, string> BuildTable(ResourceDictionary source)
    {
        var table = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is not string key || !key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (entry.Value is string text) table[key.Substring(Prefix.Length)] = text;
        }

        return table;
    }

    private static void ApplyCulture(string lang)
    {
        var culture = lang switch
        {
            "ru" => new CultureInfo("ru-RU"),
            "zh" => new CultureInfo("zh-CN"),
            _ => new CultureInfo("en-US"),
        };

        
        
        culture.NumberFormat.NumberGroupSeparator = string.Empty;

        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        
        
        
        if (_culturePinned) return;
        _culturePinned = true;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }
}
