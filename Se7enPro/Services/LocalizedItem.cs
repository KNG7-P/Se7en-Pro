using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Se7enPro.Services;

public abstract record LocalizedItem : INotifyPropertyChanged
{
    private static readonly object InstancesLock = new();
    private static readonly List<WeakReference<LocalizedItem>> Instances = new();

    static LocalizedItem()
    {
        Loc.Changed += DispatchLocaleChanged;
    }

    private static void DispatchLocaleChanged()
    {
        List<LocalizedItem> alive;
        lock (InstancesLock)
        {
            alive = new List<LocalizedItem>(Instances.Count);
            for (var i = Instances.Count - 1; i >= 0; i--)
            {
                if (Instances[i].TryGetTarget(out var target))
                {
                    alive.Add(target);
                }
                else
                {
                    Instances.RemoveAt(i);
                }
            }
        }

        foreach (var item in alive)
        {
            item.OnLocaleChanged();
        }
    }

    protected LocalizedItem()
    {
        lock (InstancesLock)
        {
            Instances.Add(new WeakReference<LocalizedItem>(this));
        }
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
