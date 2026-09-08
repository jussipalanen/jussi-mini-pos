using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JussiMiniPos.Services;

/// <summary>
/// The Google Gemini call the AI assistant makes. One request, one answer: no
/// chat history is kept, so every question is priced on its own.
/// </summary>
public sealed class GeminiClient(AiSettings settings)
{
    /// <summary>
    /// One instance for the process. A fresh HttpClient per request leaks
    /// sockets, and this one is stateless apart from its connection pool — the
    /// API key travels on the request, not on the client.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Checks that the configured key and model actually work. Deliberately
    /// goes through <see cref="GenerateJsonAsync"/> rather than pinging
    /// something cheaper: a key that can list models but not generate, or a
    /// model that does not support structured output, would both pass a
    /// simpler test and then fail in the till. The request is a few tokens.
    /// </summary>
    /// <exception cref="AssistantException">The test did not get through.</exception>
    public async Task TestAsync(CancellationToken cancellationToken) =>
        await GenerateJsonAsync(
                "You are a connection test. Answer only with the JSON you are asked for.",
                "Reply with ok set to true.",
                new
                {
                    Type = "OBJECT",
                    Properties = new { Ok = new { Type = "BOOLEAN" } },
                    Required = new[] { "ok" },
                },
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Asks the model to fill in <paramref name="responseSchema"/> and returns
    /// the JSON text it produced. Structured output is a Gemini feature rather
    /// than a prompt instruction, so the answer needs no salvaging from prose.
    /// </summary>
    /// <exception cref="AssistantException">
    /// The API refused, was unreachable, or answered with nothing usable.
    /// </exception>
    public async Task<string> GenerateJsonAsync(
        string systemInstruction,
        string prompt,
        object responseSchema,
        CancellationToken cancellationToken)
    {
        if (!settings.HasApiKey)
        {
            throw new AssistantException("Gemini API -avainta ei ole määritetty.");
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Endpoint}{Uri.EscapeDataString(settings.Model)}:generateContent")
        {
            Content = JsonContent.Create(
                new
                {
                    SystemInstruction = new { Parts = new[] { new { Text = systemInstruction } } },
                    Contents = new[]
                    {
                        new { Role = "user", Parts = new[] { new { Text = prompt } } },
                    },
                    GenerationConfig = new
                    {
                        // The catalogue is the only source of truth here, so
                        // there is nothing to gain from a creative sampling
                        // temperature.
                        Temperature = 0.2,
                        ResponseMimeType = "application/json",
                        ResponseSchema = responseSchema,
                        MaxOutputTokens = 1024,
                    },
                },
                options: RequestJson),
        };

        // Header rather than a query parameter, so the key stays out of logs
        // and out of anything that records request URLs.
        request.Headers.Add("x-goog-api-key", settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantException($"Yhteys Gemini-rajapintaan ei onnistunut: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AssistantException("Gemini-rajapinta ei vastannut ajoissa.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new AssistantException(
                    $"Gemini vastasi virheellä {(int)response.StatusCode}: {ErrorMessage(body)}");
            }

            return ExtractText(body);
        }
    }

    /// <summary>
    /// The message out of an error body, which is where the useful part of a
    /// 400 lives — a bad model name or an invalid key both land there.
    /// </summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
            // Not every failure comes back as JSON; fall through to the body.
        }

        return body.Length == 0 ? "ei lisätietoja." : Shorten(body);
    }

    /// <summary>
    /// The generated text, glued back together from the answer's parts.
    /// </summary>
    private static string ExtractText(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AssistantException("Gemini-vastausta ei voitu lukea.", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            // A blocked prompt comes back with no candidates at all, and the
            // reason is the only thing worth reporting.
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                throw new AssistantException($"Gemini ei antanut vastausta ({BlockReason(root)}).");
            }

            var candidate = candidates[0];

            // Structured output that ran out of room is truncated JSON, which
            // would otherwise fail later as an unexplained parse error.
            if (candidate.TryGetProperty("finishReason", out var finish)
                && finish.GetString() is "MAX_TOKENS")
            {
                throw new AssistantException("Gemini-vastaus katkesi kesken. Kysy lyhyemmin.");
            }

            var text = string.Empty;
            if (candidate.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var partText))
                    {
                        text += partText.GetString();
                    }
                }
            }

            if (text.Length == 0)
            {
                throw new AssistantException("Gemini palautti tyhjän vastauksen.");
            }

            return text;
        }
    }

    private static string BlockReason(JsonElement root) =>
        root.TryGetProperty("promptFeedback", out var feedback)
        && feedback.TryGetProperty("blockReason", out var reason)
        && reason.GetString() is { Length: > 0 } text
            ? text
            : "ei syytä";

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";
}

/// <summary>
/// Something went wrong on the way to an answer. The message is Finnish and
/// meant for the user, because that is where it is shown.
/// </summary>
public sealed class AssistantException(string message, Exception? innerException = null)
    : Exception(message, innerException);
