namespace JussiMiniPos.Models;

/// <summary>
/// A catalogue category, as stored in the Categories table. Categories nest
/// through <paramref name="ParentId"/>.
/// </summary>
/// <param name="Id">Row id. The filter chips select by this, never by title.</param>
/// <param name="Title">Name shown to the user.</param>
/// <param name="ParentId">Parent category, or null for a root category.</param>
public sealed record Category(int Id, string Title, int? ParentId)
{
    /// <summary>
    /// The synthetic "show everything" chip. Id 0 never collides with a real
    /// row, because SQLite's AUTOINCREMENT starts at 1.
    /// </summary>
    public static Category All { get; } = new(0, "Kaikki", null);

    public bool IsAll => Id == All.Id;
}
