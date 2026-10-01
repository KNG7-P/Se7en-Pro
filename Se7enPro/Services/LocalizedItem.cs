using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Se7enPro.Services;

public abstract record LocalizedItem : INotifyPropertyChanged
{
    protected LocalizedItem()
    {
        Loc.Changed += OnLocaleChanged;
    }

    private void OnLocaleChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;

    public virtual bool Equals(LocalizedItem? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        public override string? ToString()
    {
        var label = LabelsPerType.GetOrAdd(GetType(), t => t.GetProperty("Display"));
        return label?.GetValue(this) as string ?? base.ToString();
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> LabelsPerType = new();
}
