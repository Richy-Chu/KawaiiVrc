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

        NotifyReset();
    }

    /// <summary>Adds a batch with one Reset instead of one notification per row.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        var any = false;
        foreach (var item in items)
        {
            Items.Add(item);
            any = true;
        }

        if (any)
        {
            NotifyReset();
        }
    }

    /// <summary>Inserts a batch at the top with one Reset instead of per-row churn.</summary>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        var added = 0;
        foreach (var item in items)
        {
            Items.Insert(index + added, item);
            added++;
        }

        if (added > 0)
        {
            NotifyReset();
        }
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
