using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JussiMiniPos.Services;

/// <summary>
/// Keyword search over the Products table, used as the retrieval half of the
/// AI assistant: it picks the handful of catalogue rows a question could
/// plausibly be about, and those rows — and only those — are what the model is
/// allowed to recommend from.
///
/// The scoring runs in SQLite rather than in memory, so the query stays a
/// query: a catalogue of thousands still sends a few dozen rows to the model.
/// </summary>
public sealed class ProductSearch(Database database)
{
    /// <summary>
    /// Most rows the model is ever shown. Small enough to stay cheap, large
    /// enough that a whole demo catalogue still fits.
    /// </summary>
    public const int MaxCandidates = 40;

    /// <summary>
    /// Below this many keyword hits the result is topped up with other
    /// products, so a question like "Mitä sopii kahvin kanssa?" — which only
    /// matches the coffee itself — still leaves the model something to pair it
    /// with.
    /// </summary>
    private const int MinCandidates = 12;

    /// <summary>
    /// Finnish question words, fillers and units. They turn up in most
    /// questions and match half the catalogue, so they carry no signal.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "mitä", "mikä", "mikään", "millainen", "kuinka", "sopii", "sopiva", "sopivaa", "kanssa",
        "etsi", "hae", "näytä", "suosittele", "suositus", "haluan", "haluaisin", "saisinko",
        "voitko", "kiitos", "olisi", "voisi", "minulle", "meille", "asiakas", "asiakkaalle",
        "alle", "yli", "noin", "enintään", "korkeintaan", "vähintään", "euron", "euroa", "euro",
        "eur", "hinta", "hintaan", "hintaista", "maksaa", "senttiä", "kpl", "kappaletta",
        "tuote", "tuotteita", "tuotetta", "tuotteet", "jotain", "jotakin", "kaikki", "muuta",
        "sekä", "että", "kun", "niin", "tai", "joka", "olen", "ole",
    };

    /// <summary>Words that ask for the cheap end of the catalogue.</summary>
    private static readonly string[] CheapWords = ["halp", "halv", "edulli", "budje", "sääst"];

    /// <summary>
    /// A price ceiling written out in Finnish: "alle 10 euron", "max 5 €",
    /// "enintään 3,50". The number is allowed a few characters of slack after
    /// the limiting word, so "alle n. 10 euron" still reads.
    /// </summary>
    private static readonly Regex PriceLimit = new(
        @"(?:alle|enint[aä]{1,2}n|korkeintaan|max\.?|maks\.?|halvempi\s+kuin)\D{0,10}(\d+(?:[.,]\d{1,2})?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What a question boiled down to, before it reaches the database.</summary>
    /// <param name="Stems">Search stems, already lower-cased.</param>
    /// <param name="MaxPrice">Price ceiling in euros, when the question named one.</param>
    /// <param name="PreferCheap">True when the question asked for something cheap.</param>
    public sealed record Terms(IReadOnlyList<string> Stems, decimal? MaxPrice, bool PreferCheap);

    /// <summary>
    /// The products a question could be about, best match first. Public rows
    /// only: the assistant sells out of the same catalogue as the till.
    /// </summary>
    public IReadOnlyList<int> FindProductIds(string question)
    {
        var terms = Parse(question);

        // Keyword hits first, in score order.
        IReadOnlyList<int> ids = terms.Stems.Count == 0 ? [] : Rank(terms, minScore: 1);

        if (ids.Count >= MinCandidates)
        {
            return ids;
        }

        // Then whatever else fits the price ceiling, so the model can reason
        // across the catalogue rather than only over the literal matches.
        // Taken only up to the ceiling: both passes are capped at
        // MaxCandidates individually, so concatenating them unchecked could
        // hand the model 51 rows when the constant promises 40.
        var found = ids.ToHashSet();

        return
        [
            .. ids,
            .. Rank(terms, minScore: 0)
                .Where(id => !found.Contains(id))
                .Take(MaxCandidates - ids.Count),
        ];
    }

    /// <summary>
    /// Splits a question into search stems and the constraints hiding in it.
    /// </summary>
    /// <remarks>
    /// Finnish inflects the words a cashier types — "kahvin", "juomia" — so a
    /// whole-word match against "Kahvi" or "Juomat" would find nothing.
    /// Dropping the last couple of characters, but never below four, gets
    /// "kahvin" to "kahv" and "juomia" to "juom" without a real stemmer.
    /// </remarks>
    public static Terms Parse(string question)
    {
        var text = question ?? string.Empty;

        decimal? maxPrice = null;
        if (PriceLimit.Match(text) is { Success: true } limit
            && decimal.TryParse(
                limit.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            maxPrice = parsed;
        }

        var stems = new List<string>();
        foreach (var word in Words(text))
        {
            if (word.Length < 3 || StopWords.Contains(word))
            {
                continue;
            }

            var stem = word[..Math.Min(word.Length, Math.Max(4, word.Length - 2))];
            if (!stems.Contains(stem))
            {
                stems.Add(stem);
            }
        }

        // "halpa" inflects too, so the stem and the marker are compared from
        // whichever end is shorter: "halv" against "halvempaa" and back.
        var preferCheap = stems.Any(stem => CheapWords.Any(cheap =>
            stem.StartsWith(cheap, StringComparison.Ordinal)
            || cheap.StartsWith(stem, StringComparison.Ordinal)));

        return new Terms(stems, maxPrice, preferCheap);
    }

    /// <summary>
    /// Lower-cased runs of letters. Digits are dropped: the only number that
    /// matters is the price ceiling and <see cref="PriceLimit"/> already has
    /// it, so a stray "0,5 l" in a question does not become a search term.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    /// <summary>
    /// Scores every public product against the stems and returns the best
    /// <see cref="MaxCandidates"/> ids. A title hit counts for most, a
    /// category hit less and a description hit least, so "juomia" ranks the
    /// drinks above a cake that merely mentions one.
    /// </summary>
    private IReadOnlyList<int> Rank(Terms terms, int minScore)
    {
        using var connection = database.OpenConnection();
        var dialect = database.Dialect;

        // Case folding has to understand "Äyriäiset" so that "äyri" matches
        // it. SQLite's own lower() and LIKE fold ASCII only, so there the job
        // goes to a .NET callback; PostgreSQL's lower() already knows. Which
        // of the two happens is the dialect's business.
        dialect.PrepareSearch(connection);

        using var command = connection.CreateCommand();

        var score = new StringBuilder("0");
        for (var i = 0; i < terms.Stems.Count; i++)
        {
            score.Append($" + (CASE WHEN {dialect.Fold("Title")} LIKE @stem{i} THEN 8 ELSE 0 END)");
            score.Append($" + (CASE WHEN {dialect.Fold("CategoryTitles")} LIKE @stem{i} THEN 4 ELSE 0 END)");
            score.Append($" + (CASE WHEN {dialect.Fold("Description")} LIKE @stem{i} THEN 2 ELSE 0 END)");

            Database.AddParameter(command, $"@stem{i}", $"%{Escape(terms.Stems[i])}%");
        }

        // The price the till would actually charge, which is what a price
        // ceiling in the question is about.
        var effective = dialect.Least("PriceCents", "COALESCE(SalePriceCents, PriceCents)");

        // "Halpa" asks for the cheap end, so price leads the ordering and
        // relevance breaks its ties. It only reshuffles within a set that has
        // already been filtered by score, and the top-up pass appends only
        // rows the keyword pass did not already return, so asking for
        // something cheap cannot push a keyword match below a bargain that
        // has nothing to do with the question.
        var order = terms.PreferCheap
            ? "EffectiveCents ASC, Score DESC, Id ASC"
            : "Score DESC, EffectiveCents ASC, Id ASC";

        // The offer price is what the till charges, so that is the price an
        // "alle 10 euron" question is about. As in Product, SalePriceCents
        // only counts when it actually undercuts the normal price.
        //
        // Score is worked out in a subquery because a WHERE cannot see an
        // output alias of its own SELECT.
        command.CommandText =
            $"""
            SELECT Id FROM (
                SELECT Id,
                       {effective} AS EffectiveCents,
                       {score} AS Score
                FROM (
                    SELECT Id, Title, Description, PriceCents, SalePriceCents,
                           (SELECT COALESCE({dialect.GroupConcat("c.Title", "' '")}, '')
                              FROM ProductCategories pc
                              JOIN Categories c ON c.Id = pc.CategoryId
                             WHERE pc.ProductId = p.Id) AS CategoryTitles
                    FROM Products p
                    WHERE p.IsPublic = 1
                ) AS expanded
                WHERE @maxPriceCents IS NULL
                   OR {effective} <= @maxPriceCents
            ) AS ranked
            WHERE Score >= @minScore
            ORDER BY {order}
            LIMIT @limit;
            """;

        Database.AddParameter(command, "@minScore", minScore);
        Database.AddParameter(command, "@limit", MaxCandidates);
        Database.AddParameter(
            command,
            "@maxPriceCents",
            terms.MaxPrice is { } max ? SalesRepository.ToCents(max) : null,
            DbType.Int64);

        using var reader = command.ExecuteReader();

        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    /// <summary>
    /// Neutralises LIKE's own wildcards so a question containing % or _ is
    /// searched for literally. The pattern carries no ESCAPE clause, so the
    /// characters are dropped from the stem rather than escaped in it.
    /// </summary>
    private static string Escape(string stem) =>
        stem.Replace("%", string.Empty).Replace("_", string.Empty);
}
