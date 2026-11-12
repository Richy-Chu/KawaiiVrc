using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Lets a large list be swapped in one shot. Adding thousands of rows one
/// notification at a time is what made a big friend list take half a minute;
/// a single Reset lets the virtualizing panel build only the visible rows.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
