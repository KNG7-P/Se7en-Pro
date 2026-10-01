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
        var added = 0;
        foreach (var item in items)
        {
            Items.Add(item);
            added++;
        }
        if (added == 0) return;
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(CollectionReset);
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
