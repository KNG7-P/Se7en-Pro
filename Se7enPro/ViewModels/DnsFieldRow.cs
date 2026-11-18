using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public enum DnsFieldState
{
    Empty,

    Ok,

    Problem,
}

public sealed partial class DnsFieldRow : ObservableObject
{
    private readonly Action<DnsFieldRow> _changed;
    private readonly Func<string> _label;
    private readonly Func<string> _hint;
    private readonly Func<string> _placeholder;

    public DnsFieldRow(
        DnsTransport transport,
        Func<string> label,
        Func<string> hint,
        Func<string> placeholder,
        Action<DnsFieldRow> changed)
    {
        Transport = transport;
        _label = label;
        _hint = hint;
        _placeholder = placeholder;
        _changed = changed;
    }

    public DnsTransport Transport { get; }
    public string Label => _label();
    public string Hint => _hint();
    public string Placeholder => _placeholder();

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(Placeholder));
        Revalidate();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    private string _text = "";

    public List<DnsServerEntry> Entries { get; private set; } = new();

    public DnsFieldState State { get; private set; } = DnsFieldState.Empty;

    public string Problem { get; private set; } = "";

    public bool HasProblem => State == DnsFieldState.Problem;
    public bool IsEmpty => State == DnsFieldState.Empty;
    public bool HasEntries => Entries.Count > 0;

    public string CountText =>
        Entries.Count == 0 ? "" : Entries.Count.ToString(CultureInfo.InvariantCulture);

    partial void OnTextChanged(string value)
    {
        Revalidate();
        _changed(this);
    }

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