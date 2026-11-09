using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.App.ViewModels;

/// <summary><see cref="Default"/> keeps the order the location gave (Home: pin order, newest recent file first).</summary>
public enum SortColumn { Name, Modified, Type, Size, Created, Property, Default }

public sealed class ItemComparer(SortColumn column, bool ascending, string? property = null, bool byGroup = false) : IComparer<ItemViewModel>
{
    public int Compare(ItemViewModel? x, ItemViewModel? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        // Groups keep their own order whatever the sort direction inside them.
        if (byGroup && x.GroupRank != y.GroupRank) return x.GroupRank.CompareTo(y.GroupRank);

        int r;
        if (column == SortColumn.Default)
        {
            r = x.Entry.SectionOrder.CompareTo(y.Entry.SectionOrder);
            return ascending ? r : -r;
        }
        if (x.IsFolder != y.IsFolder) r = x.IsFolder ? -1 : 1;
        else
        {
            r = column switch
            {
                SortColumn.Modified => Nullable.Compare(x.Entry.Modified, y.Entry.Modified),
                SortColumn.Created => Nullable.Compare(x.Entry.Created, y.Entry.Created),
                SortColumn.Type => string.Compare(x.TypeName, y.TypeName, StringComparison.CurrentCultureIgnoreCase),
                SortColumn.Size => x.SortSize.CompareTo(y.SortSize),
                SortColumn.Property when property is not null => CompareValues(x.GetSortValue(property), y.GetSortValue(property)),
                _ => 0,
            };
            if (r == 0) r = NaturalComparer.Instance.Compare(x.Entry.DisplayName, y.Entry.DisplayName);
        }
        return ascending ? r : -r;
    }

    private static int CompareValues(IComparable? a, IComparable? b)
    {
        // Items without the property sort after those with it, as in Explorer.
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        if (a.GetType() == b.GetType()) return a.CompareTo(b);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }
}
