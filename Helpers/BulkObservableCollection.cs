using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace R2Cmd;

/// <summary>
/// An ObservableCollection that can change many items at once while raising a
/// single notification.
///
/// Used for the file panes: repopulating a folder with thousands of entries via
/// individual Add calls is O(n) UI notifications at best, and if the ListView's
/// CollectionView has active sort descriptions, WPF re-positions each item into
/// its sorted slot as it arrives, which is effectively O(n^2) for a full refresh.
/// A single Reset lets WPF re-read and re-sort the list once.
///
/// WPF's ListCollectionView does not accept range notifications (an Add or
/// Remove carrying several items throws "Range actions are not supported"), so
/// every bulk operation here chooses between two legal shapes:
///   - a handful of items: ordinary per-item notifications, which keep the
///     realized containers, the scroll position and the selection intact;
///   - anything larger: one Reset, which is far cheaper than N layout passes.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Up to this many items a change is reported item by item. A Reset
    /// regenerates every visible row container and may drop the selection, which
    /// is not worth it for a couple of rows.
    /// </summary>
    public const int IndividualNotificationLimit = 16;

    // Immutable, so one instance per closed generic type is enough. Allocating
    // them per call was pure garbage on the refresh path.
    private static readonly PropertyChangedEventArgs s_countChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs s_indexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs s_resetChanged =
        new(NotifyCollectionChangedAction.Reset);

    // Shrink the backing array after leaving a huge folder, but only when the
    // waste is significant: re-growing it on the next big folder costs a copy.
    private const int TrimCapacityThreshold = 1024;

    // Every ObservableCollection constructor stores the items in a List<T>, so
    // this cast is safe. The List<T> gives us AddRange / RemoveAll, which work on
    // the raw array instead of going through the virtual Collection<T> methods.
    private List<T> Backing => (List<T>)Items;

    /// <summary>
    /// Replaces the whole contents with <paramref name="newItems"/> and raises a
    /// single Reset.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);
        CheckReentrancy();

        // The source must be materialized BEFORE clearing: a lazy query over this
        // collection (or this collection itself) would otherwise read an empty list.
        IEnumerable<T> source = IsSafeSnapshot(newItems) ? newItems : new List<T>(newItems);

        var backing = Backing;
        backing.Clear();
        backing.AddRange(source);

        if (backing.Capacity > TrimCapacityThreshold && backing.Count < backing.Capacity / 4)
            backing.TrimExcess();

        RaiseReset();
    }

    /// <summary>
    /// Appends <paramref name="newItems"/> to the end of the collection.
    /// </summary>
    public void AddRange(IEnumerable<T> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);

        IList<T> source = newItems is IList<T> list && IsSafeSnapshot(list)
            ? list
            : new List<T>(newItems);

        if (source.Count == 0) return;

        if (source.Count <= IndividualNotificationLimit)
        {
            foreach (var item in source) Add(item);
            return;
        }

        CheckReentrancy();
        Backing.AddRange(source);
        RaiseReset();
    }

    /// <summary>
    /// Removes every item contained in <paramref name="itemsToRemove"/>.
    /// Returns the number of items actually removed.
    /// </summary>
    /// <remarks>
    /// Membership is tested with the default equality comparer of T. For
    /// FileEntry, which does not override Equals, that is reference equality.
    /// In the bulk path all occurrences of a matching item are removed; the
    /// collections this is used for never contain the same instance twice.
    /// </remarks>
    public int RemoveRange(IEnumerable<T> itemsToRemove)
    {
        ArgumentNullException.ThrowIfNull(itemsToRemove);
        if (Count == 0) return 0;

        // One set for the whole call: O(1) membership instead of a linear
        // Contains per element, which is what made the old path quadratic
        var set = itemsToRemove as HashSet<T> ?? new HashSet<T>(itemsToRemove);
        if (set.Count == 0) return 0;

        if (set.Count <= IndividualNotificationLimit)
        {
            int removedOneByOne = 0;
            foreach (var item in set)
            {
                if (Remove(item)) removedOneByOne++;
            }
            return removedOneByOne;
        }

        CheckReentrancy();

        // List<T>.RemoveAll compacts the array in a single linear pass
        int removed = Backing.RemoveAll(set.Contains);
        if (removed > 0) RaiseReset();

        return removed;
    }

    /// <summary>
    /// Removes every item matching <paramref name="match"/>.
    /// Returns the number of items removed.
    /// </summary>
    /// <remarks>
    /// The predicate is evaluated twice per item (count first, then remove), so
    /// it must not have side effects.
    /// </remarks>
    public int RemoveAll(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        var backing = Backing;

        int matches = 0;
        for (int i = 0; i < backing.Count; i++)
        {
            if (match(backing[i])) matches++;
        }

        if (matches == 0) return 0;

        if (matches <= IndividualNotificationLimit)
        {
            // Walking backwards keeps the remaining indices valid
            for (int i = backing.Count - 1; i >= 0; i--)
            {
                if (match(backing[i])) RemoveAt(i);
            }
            return matches;
        }

        CheckReentrancy();
        backing.RemoveAll(match);
        RaiseReset();

        return matches;
    }

    // A source can be consumed after the backing list has been modified only if
    // it is a separate, already materialized collection
    private bool IsSafeSnapshot(IEnumerable<T> source) =>
        source is ICollection<T> &&
        !ReferenceEquals(source, this) &&
        !ReferenceEquals(source, Items);

    private void RaiseReset()
    {
        OnPropertyChanged(s_countChanged);
        OnPropertyChanged(s_indexerChanged);
        OnCollectionChanged(s_resetChanged);
    }
}
