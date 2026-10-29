using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JussiMiniPos.Services;

/// <summary>
/// The AI assistant's configuration: whether it is switched on, which Gemini
/// model it calls, and the API key it calls with. The first two live in the
/// Options table; the key lives in a file beside the database, because it is a
/// secret and the database file is the thing users copy around as a backup.
/// </summary>
/// <param name="IsEnabled">False hides the assistant from the till entirely.</param>
/// <param name="ApiKey">The Gemini API key, or null when none is configured.</param>
/// <param name="Model">The Gemini model to call.</param>
public sealed record AiSettings(bool IsEnabled, string? ApiKey, string Model)
{
    /// <summary>
    /// The cheapest Gemini model with a free tier, which is all this feature
    /// needs — the question is short and the catalogue is small.
    /// </summary>
    /// <remarks>
    /// Google retires these. When a model goes, the API answers 404 with the
    /// name of its replacement, and that arrives as the assistant's notice
    /// band rather than as a crash — so the fix is picking another model in
    /// Admin, and this constant only decides where a fresh install starts.
    /// </remarks>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    /// <summary>Option names, as stored in the Options table.</summary>
    public const string EnabledOption = "Ai.Enabled";

    public const string ModelOption = "Ai.Model";

    /// <summary>Environment variable holding the API key.</summary>
    public const string ApiKeyVariable = "GEMINI_API_KEY";

    /// <summary>Environment variable used when no model has been chosen.</summary>
    public const string ModelVariable = "GEMINI_MODEL";

    /// <summary>File name of the key file, kept beside the database.</summary>
    public const string KeyFileName = "gemini.key";

    /// <summary>True when there is a key to call the API with.</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// The models offered in Admin, cheapest first. Every one of these was
    /// checked against the API rather than taken from documentation: being
    /// listed by the models endpoint is not the same as being callable, and a
    /// retired model is still listed.
    /// </summary>
    public static IReadOnlyList<ModelChoice> Models { get; } =
    [
        new(DefaultModel, "Halvin ja nopein. Suositus kassakäyttöön."),
        new("gemini-flash-lite-latest", "Aina uusin kevyt malli. Ei vanhene."),
        new("gemini-3.1-flash-lite", "Edellinen kevyt malli."),
        new("gemini-3.5-flash", "Tarkempi ja kalliimpi kuin kevyt malli."),
        new("gemini-flash-latest", "Aina uusin Flash-malli. Kallein näistä."),
    ];

    /// <summary>One entry in the model list.</summary>
    /// <param name="Id">The model name the API is called with.</param>
    /// <param name="Description">What choosing it means, in Finnish.</param>
    public sealed record ModelChoice(string Id, string Description);

    /// <summary>
    /// Reads the current configuration. A stored option wins over the
    /// matching environment variable: Admin is where a user expects to be
    /// in charge, so the variables are the fallback for a machine that has
    /// never been configured rather than an override of one that has.
    /// </summary>
    public static AiSettings Load(OptionsRepository options, string? dataDirectory)
    {
        var model = options.Get(ModelOption);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = Environment.GetEnvironmentVariable(ModelVariable);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        return new AiSettings(
            options.GetBool(EnabledOption, fallback: true),
            ReadApiKey(dataDirectory),
            model.Trim());
    }

    /// <summary>Where the key file is looked for, for the message that says so.</summary>
    public static string KeyFilePath(string? dataDirectory) =>
        Path.Combine(dataDirectory ?? string.Empty, KeyFileName);

    /// <summary>True when the environment is supplying the key, so nothing is stored.</summary>
    public static bool HasApiKeyFromEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable));

    /// <summary>True when a key file is there to be read.</summary>
    public static bool HasStoredApiKey(string? dataDirectory) =>
        ReadKeyFile(dataDirectory) is not null;

    /// <summary>
    /// Marks a key file as DPAPI-encrypted. Its absence means the file is a
    /// plain key, which is what earlier versions wrote and what someone
    /// setting the key up by hand will write.
    /// </summary>
    private const string EncryptedPrefix = "DPAPI:";

    /// <summary>
    /// Writes the key file, or deletes it when <paramref name="apiKey"/> is
    /// blank. Deleting rather than storing an empty file keeps "no key" a
    /// single state.
    /// </summary>
    /// <remarks>
    /// The key is encrypted with Windows DPAPI under the current user account,
    /// so the file is useless to another account on the machine and useless in
    /// a backup copied to another machine. It does not defend against code
    /// running as this user — that code can simply ask DPAPI to decrypt it —
    /// which is the honest limit of storing a key a program has to be able to
    /// read on its own.
    /// </remarks>
    public static void SaveApiKey(string? dataDirectory, string? apiKey)
    {
        if (string.IsNullOrEmpty(dataDirectory))
        {
            throw new InvalidOperationException("Tietokannan kansiota ei tiedetä.");
        }

        var path = KeyFilePath(dataDirectory);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(dataDirectory);

        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        string contents;

        try
        {
            var encrypted = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            contents = EncryptedPrefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // Rather than fail the save, fall back to the plain key and let it
            // keep working. Storing it unencrypted is worse than encrypting it
            // and much better than an assistant that cannot be configured.
            contents = apiKey.Trim();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        // No BOM and no trailing newline, so the file holds exactly the value.
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    private static string? ReadApiKey(string? dataDirectory)
    {
        // The file first: it is what Admin writes, and a saved key has to
        // take effect. The variable covers a machine that was never set up.
        if (ReadKeyFile(dataDirectory) is { } stored)
        {
            return stored;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    private static string? ReadKeyFile(string? dataDirectory)
    {
        if (string.IsNullOrEmpty(dataDirectory))
        {
            return null;
        }

        try
        {
            // Read rather than tested for first: File.Exists followed by a read
            // is two answers to the same question, and only the read matters.
            // ReadAllText strips a byte order mark, so a key file written by a
            // text editor works as well as one written here.
            var contents = File.ReadAllText(KeyFilePath(dataDirectory)).Trim();

            if (contents.Length == 0)
            {
                return null;
            }

            // A file without the marker is a plain key: what earlier versions
            // wrote, and what someone setting this up by hand will write. Both
            // keep working, and saving from Admin encrypts whatever is there.
            if (!contents.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                return contents;
            }

            var encrypted = Convert.FromBase64String(contents[EncryptedPrefix.Length..]);
            var plaintext = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);

            try
            {
                return Encoding.UTF8.GetString(plaintext).Trim();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted for a different Windows account, or the file is
            // damaged. Either way there is no key to be had; the assistant
            // falls back to plain search and says so.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No key file, or one that cannot be read. Either way the assistant
            // falls back to plain catalogue search and says why.
            return null;
        }
    }
}
