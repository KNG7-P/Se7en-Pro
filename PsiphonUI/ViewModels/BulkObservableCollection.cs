using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PsiphonUI.ViewModels;

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
}
