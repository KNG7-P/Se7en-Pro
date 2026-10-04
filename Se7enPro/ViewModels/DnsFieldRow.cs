using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

/// <summary>Health of one resolver list, used to colour its row.</summary>
public enum DnsFieldState
{
    /// <summary>Nothing entered: the built-in defaults answer.</summary>
    Empty,

    /// <summary>Everything entered parses and is usable.</summary>
    Ok,

    /// <summary>Something is entered that will be ignored.</summary>
    Problem,
}

/// <summary>
/// Represents a bindable DNS resolver configuration row.
/// </summary>
public sealed partial class DnsFieldRow : ObservableObject
{
    private readonly Action<DnsFieldRow> _changed;

    public DnsFieldRow(
        DnsTransport transport,
        string label,
        string hint,
        string placeholder,
        Action<DnsFieldRow> changed)
    {
        Transport = transport;
        Label = label;
        Hint = hint;
        Placeholder = placeholder;
        _changed = changed;
    }

    public DnsTransport Transport { get; }
    public string Label { get; }
    public string Hint { get; }
    public string Placeholder { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    private string _text = "";

    /// <summary>Parsed resolver entries.</summary>
    public List<DnsServerEntry> Entries { get; private set; } = new();

    public DnsFieldState State { get; private set; } = DnsFieldState.Empty;

    /// <summary>Validation error message, if any.</summary>
    public string Problem { get; private set; } = "";

    public bool HasProblem => State == DnsFieldState.Problem;
    public bool IsEmpty => State == DnsFieldState.Empty;
    public bool HasEntries => Entries.Count > 0;

    /// <summary>Entry count for display badge.</summary>
    public string CountText =>
        Entries.Count == 0 ? "" : Entries.Count.ToString(CultureInfo.InvariantCulture);

    partial void OnTextChanged(string value)
    {
        Revalidate();
        _changed(this);
    }

    /// <summary>
    /// Validates and parses the resolver entry list.
    /// </summary>
    public void Revalidate()
    {
        var entries = DnsSettings.ParseList(Transport, Text, out var problems);

        Entries = entries;
        Problem = problems.Count == 0
            ? ""
            : string.Join("  ·  ", problems.Take(2))
              + (problems.Count > 2 ? $"  (+{problems.Count - 2})" : "");

        State = problems.Count > 0
            ? DnsFieldState.Problem
            : entries.Count > 0
                ? DnsFieldState.Ok
                : DnsFieldState.Empty;

        OnPropertyChanged(nameof(Problem));
    }
}