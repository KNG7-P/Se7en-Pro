using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Se7enPro.ViewModels;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new("Count");
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs CollectionReset =
        new(NotifyCollectionChangedAction.Reset);

    public void ResetWith(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(CollectionReset);
    }

    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        var index = Items.Count;
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, item, index++));
            added = true;
        }
        if (!added) return;
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
    }

    public void RemoveFirst(int count)
    {
        CheckReentrancy();
        count = Math.Min(count, Items.Count);
        if (count <= 0) return;
        for (var i = 0; i < count; i++)
        {
            var item = Items[0];
            Items.RemoveAt(0);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, item, 0));
        }
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
    }

    public void RemoveRange(IEnumerable<T> itemsToRemove)
    {
        CheckReentrancy();
        var set = itemsToRemove as HashSet<T> ?? new HashSet<T>(itemsToRemove);
        if (set.Count == 0) return;
        if (Items is List<T> list)
        {
            var removed = list.RemoveAll(set.Contains);
            if (removed == 0) return;
        }
        else
        {
            var remaining = new List<T>(Items.Count);
            foreach (var item in Items)
            {
                if (!set.Contains(item))
                    remaining.Add(item);
            }
            if (remaining.Count == Items.Count) return;
            Items.Clear();
            foreach (var item in remaining)
                Items.Add(item);
        }
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(CollectionReset);
    }
}
