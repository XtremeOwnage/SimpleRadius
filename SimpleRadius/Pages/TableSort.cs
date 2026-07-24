namespace SimpleRadius.Pages;

/// <summary>
/// State for a sortable table header. Sorting is applied on the server so it stays correct when a list is
/// filtered or capped, and it works with JavaScript disabled.
/// </summary>
public sealed class TableSort
{
    public TableSort(string? column, bool descending, string defaultColumn)
    {
        Column = string.IsNullOrWhiteSpace(column) ? defaultColumn : column;
        Descending = descending;
    }

    public string Column { get; }

    public bool Descending { get; }

    public bool IsSortedBy(string column) =>
        string.Equals(Column, column, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clicking the active column flips direction; any other column starts ascending.</summary>
    public bool NextDirectionFor(string column) => IsSortedBy(column) && !Descending;

    /// <summary>The arrow shown in the header, or an empty string for inactive columns.</summary>
    public string IndicatorFor(string column) =>
        !IsSortedBy(column) ? string.Empty : Descending ? "▾" : "▴";

    /// <summary>
    /// Applies the sort. <paramref name="columns"/> maps a column key to its key selector; an unknown key
    /// falls back to the first entry so a hand-edited URL cannot break the page.
    /// </summary>
    public IOrderedQueryable<T> Apply<T>(
        IQueryable<T> query,
        IReadOnlyDictionary<string, System.Linq.Expressions.Expression<Func<T, object?>>> columns)
    {
        if (!columns.TryGetValue(Column, out var selector))
        {
            selector = columns.Values.First();
        }

        return Descending ? query.OrderByDescending(selector) : query.OrderBy(selector);
    }
}
