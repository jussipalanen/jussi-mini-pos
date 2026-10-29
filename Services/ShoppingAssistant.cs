using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JussiMiniPos.Models;
using System.Data.Common;

namespace JussiMiniPos.Services;

/// <summary>
/// The AI assistant behind the till: a Finnish question in, catalogue products
/// out. Retrieval augmented generation, in the plain sense — the products come
/// out of SQLite through <see cref="ProductSearch"/>, and the model only picks
/// between the rows it is handed. It cannot invent a product, a price or an
/// id, because an id that was not in the candidate list is thrown away.
/// </summary>
public sealed class ShoppingAssistant
{
    /// <summary>How many products a single answer may recommend.</summary>
    private const int MaxSuggestions = 5;

    private static readonly JsonSerializerOptions AnswerJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Structured output, so the answer arrives as JSON by contract rather
    /// than by asking nicely. The type names are the schema enum's own, which
    /// are upper case.
    /// </summary>
    private static readonly object ResponseSchema = new
    {
        Type = "OBJECT",
        Properties = new
        {
            Reply = new { Type = "STRING" },
            Suggestions = new
            {
                Type = "ARRAY",
                Items = new
                {
                    Type = "OBJECT",
                    Properties = new
                    {
                        Id = new { Type = "INTEGER" },
                        Reason = new { Type = "STRING" },
                    },
                    Required = new[] { "id", "reason" },
                    PropertyOrdering = new[] { "id", "reason" },
                },
            },
        },
        Required = new[] { "reply", "suggestions" },
        PropertyOrdering = new[] { "reply", "suggestions" },
    };

    private const string SystemInstruction =
        """
        You are the shop assistant built into JussiMiniPos, a Finnish
        point-of-sale till. A cashier types what a customer is after, in
        Finnish, and you pick products for them from the till's own catalogue.

        Rules:
        - Recommend only products from the CATALOGUE section of the message.
          Never invent a product, a price or an id, and never suggest an id
          that is not listed there.
        - Order the suggestions best first, and give at most five.
        - Obey any constraint in the question, a price ceiling above all. If a
          product breaks it, leave it out even when it fits otherwise.
        - When the question asks what goes with something, suggest things to go
          alongside it rather than the thing itself.
        - When nothing in the catalogue fits, return an empty list of
          suggestions and say so in the reply.

        Write everything in Finnish, addressed to the cashier:
        - "reply" is one or two short sentences.
        - "reason" is a short phrase, at most twelve words, saying why that
          product fits the question. Do not repeat the price in it.
        """;

    private readonly CatalogRepository _catalog;
    private readonly OptionsRepository _options;
    private readonly ProductSearch _search;
    private readonly string? _dataDirectory;

    public ShoppingAssistant(Database database, CatalogRepository catalog, OptionsRepository options)
    {
        _catalog = catalog;
        _options = options;
        _search = new ProductSearch(database);

        // The key file lives beside the database, so a till that is configured
        // once keeps working without an environment variable.
        _dataDirectory = database.DataDirectory;
    }

    /// <summary>One recommended product, with the model's reason for it.</summary>
    /// <param name="Product">The catalogue row, ready to be put in the cart.</param>
    /// <param name="Reason">Why it fits, in Finnish. Empty when there was no model answer.</param>
    public sealed record Suggestion(Product Product, string Reason);

    /// <summary>
    /// What one question produced.
    /// </summary>
    /// <param name="Reply">The assistant's own words, shown above the products.</param>
    /// <param name="Suggestions">The recommended products, best first.</param>
    /// <param name="Notice">
    /// Set when the answer is not what was asked for — no API key, or a failed
    /// call — in which case the suggestions are plain search results.
    /// </param>
    /// <param name="CandidateCount">
    /// How many catalogue rows the retrieval found and the model was shown.
    /// Nothing in the UI needs it; it is what makes <c>--ask</c> useful for
    /// telling a bad search apart from a bad answer.
    /// </param>
    public sealed record Answer(
        string Reply,
        IReadOnlyList<Suggestion> Suggestions,
        string? Notice = null,
        int CandidateCount = 0);

    /// <summary>Where the API key is expected, for the message that asks for one.</summary>
    public string KeyFilePath => AiSettings.KeyFilePath(_dataDirectory);

    /// <summary>
    /// False when the assistant has been switched off in Admin, which is
    /// what keeps its button out of the till. Read live rather than cached, so
    /// the setting takes effect the next time the till is opened.
    /// </summary>
    public bool IsEnabled => Settings.IsEnabled;

    /// <summary>True when a key is configured, so a caller can say as much.</summary>
    public bool IsConfigured => Settings.HasApiKey;

    /// <summary>The configuration as it stands right now.</summary>
    public AiSettings Settings => AiSettings.Load(_options, _dataDirectory);

    /// <summary>The folder the key file belongs in, for Admin to write to.</summary>
    public string? DataDirectory => _dataDirectory;

    /// <summary>
    /// Answers one question. Only cancellation and a broken database escape as
    /// exceptions; a missing key or a failed API call come back as an answer
    /// carrying a <see cref="Answer.Notice"/> and the raw search results, so
    /// the assistant degrades into a search box rather than a dead end.
    /// </summary>
    public async Task<Answer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new Answer("Kirjoita kysymys, niin etsin sopivat tuotteet.", []);
        }

        var settings = AiSettings.Load(_options, _dataDirectory);

        // SQLite is synchronous and the search touches every product row, so it
        // runs off the UI thread.
        var candidates = await Task.Run(() => Retrieve(question), cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return new Answer(
                "Kassan valikoimasta ei löytynyt yhtään tuotetta, joka vastaisi kysymystä.",
                []);
        }

        // Switched off in Admin. The till hides the button, so this is only
        // reached from the command line — the search still answers, which is
        // what makes --ask useful for checking the setting took.
        if (!settings.IsEnabled)
        {
            return new Answer(
                "Hakutulokset kassan valikoimasta:",
                Fallback(candidates),
                "AI-avustaja on poistettu käytöstä Admin-näkymässä.",
                candidates.Count);
        }

        if (!settings.HasApiKey)
        {
            return new Answer(
                "Hakutulokset kassan valikoimasta:",
                Fallback(candidates),
                "Gemini API -avainta ei ole määritetty, joten AI-suositukset eivät ole " +
                "käytössä. Lisää avain Admin-näkymästä.",
                candidates.Count);
        }

        var client = new GeminiClient(settings);

        try
        {
            var json = await client
                .GenerateJsonAsync(SystemInstruction, BuildPrompt(question, candidates), ResponseSchema, cancellationToken)
                .ConfigureAwait(false);

            return Parse(json, candidates);
        }
        catch (AssistantException ex)
        {
            // The retrieval worked even though the model did not, so show what
            // the search found and say why there is no AI answer.
            return new Answer(
                "Hakutulokset kassan valikoimasta:",
                Fallback(candidates),
                $"{ex.Message} Näytetään hakutulokset ilman AI-suosituksia.",
                candidates.Count);
        }
    }

    /// <summary>
    /// The candidate products, in the search's own ranking. The ids come from
    /// SQLite and the rows are read back through the repository, so a
    /// suggestion is the same <see cref="Product"/> the till sells.
    /// </summary>
    private IReadOnlyList<Product> Retrieve(string question)
    {
        try
        {
            var ids = _search.FindProductIds(question);
            if (ids.Count == 0)
            {
                return [];
            }

            var byId = _catalog.GetProducts(publicOnly: true, ids: ids).ToDictionary(p => p.Id);

            return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
        }
        catch (DbException ex)
        {
            throw new AssistantException($"Tuotehaku ei onnistunut: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The question and the retrieved rows, as the one message the model sees.
    /// Prices are the ones the till would charge, so a price limit in the
    /// question can be checked against what is written here.
    /// </summary>
    private static string BuildPrompt(string question, IReadOnlyList<Product> candidates)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("KYSYMYS:");
        prompt.AppendLine(question.Trim());
        prompt.AppendLine();
        prompt.AppendLine("CATALOGUE (id · nimi · kategoriat · hinta):");

        foreach (var product in candidates)
        {
            prompt.Append($"{product.Id} · {product.Name} · ");
            prompt.Append(product.CategoryNames.Length == 0 ? "ei kategoriaa" : product.CategoryNames);
            prompt.Append($" · {product.EffectivePrice:N2} €");

            if (product.IsOnSale)
            {
                prompt.Append($" (tarjous, normaalisti {product.Price:N2} €)");
            }

            if (!string.IsNullOrWhiteSpace(product.Description))
            {
                prompt.Append($" — {product.Description.Trim()}");
            }

            prompt.AppendLine();
        }

        return prompt.ToString();
    }

    /// <summary>
    /// Turns the model's JSON into products. Ids are looked up in the
    /// candidate list rather than trusted: an id the model made up, or one it
    /// remembered from an earlier answer, simply does not resolve.
    /// </summary>
    private static Answer Parse(string json, IReadOnlyList<Product> candidates)
    {
        ModelAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<ModelAnswer>(json, AnswerJson);
        }
        catch (JsonException ex)
        {
            throw new AssistantException("Gemini-vastaus ei ollut odotetussa muodossa.", ex);
        }

        var byId = candidates.ToDictionary(product => product.Id);
        var suggestions = new List<Suggestion>();

        foreach (var suggested in answer?.Suggestions ?? [])
        {
            if (!byId.TryGetValue(suggested.Id, out var product)
                || suggestions.Any(s => s.Product.Id == product.Id))
            {
                continue;
            }

            suggestions.Add(new Suggestion(product, (suggested.Reason ?? string.Empty).Trim()));

            if (suggestions.Count == MaxSuggestions)
            {
                break;
            }
        }

        var reply = (answer?.Reply ?? string.Empty).Trim();
        if (reply.Length == 0)
        {
            reply = suggestions.Count == 0
                ? "En löytänyt sopivia tuotteita."
                : "Nämä voisivat sopia:";
        }

        return new Answer(reply, suggestions, Notice: null, candidates.Count);
    }

    /// <summary>The top search results, for when there is no model answer.</summary>
    private static IReadOnlyList<Suggestion> Fallback(IReadOnlyList<Product> candidates) =>
        [.. candidates.Take(MaxSuggestions).Select(product => new Suggestion(product, string.Empty))];

    /// <summary>The shape <see cref="ResponseSchema"/> asks the model to fill in.</summary>
    private sealed record ModelAnswer(string? Reply, IReadOnlyList<ModelSuggestion>? Suggestions);

    private sealed record ModelSuggestion(int Id, string? Reason);
}
