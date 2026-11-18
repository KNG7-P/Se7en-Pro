using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Se7enPro.Models;

public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public ObservableRangeCollection() : base() { }
    public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

    public void AddRange(IEnumerable<T> collection)
    {
        if (collection == null) return;
        CheckReentrancy();
        var any = false;
        foreach (var item in collection)
        {
            Items.Add(item);
            any = true;
        }
        if (!any) return;

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void ReplaceAll(IEnumerable<T> collection)
    {
        CheckReentrancy();
        Items.Clear();
        if (collection != null)
        {
            foreach (var item in collection)
            {
                Items.Add(item);
            }
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
