using System;

namespace JussiMiniPos.Models;

/// <summary>One row of the audit log: who did what, and when.</summary>
/// <param name="Id">Row id, in the order things happened.</param>
/// <param name="At">When it happened.</param>
/// <param name="Actor">
/// Who did it: a username, "komentorivi (Windows-tunnus)" for the command
/// line, or null when nobody was signed in.
/// </param>
/// <param name="Action">One of the <see cref="AuditActions"/> codes.</param>
/// <param name="Details">What changed, in Finnish, for a person to read.</param>
public sealed record AuditEntry(long Id, DateTimeOffset At, string? Actor, string Action, string Details)
{
    public string ActorText => Actor ?? "—";

    public string ActionText => AuditActions.Finnish(Action);
}

/// <summary>
/// The things the audit log records. Codes are English and stored; the
/// Finnish names are what the log view shows, the same split as
/// <see cref="PaymentMethodNames"/>.
/// </summary>
public static class AuditActions
{
    public const string SignIn = "sign-in";
    public const string SignInFailed = "sign-in-failed";
    public const string SignOut = "sign-out";
    public const string SaleVoided = "sale-voided";
    public const string ProductCreated = "product-created";
    public const string ProductUpdated = "product-updated";
    public const string ProductDeleted = "product-deleted";
    public const string VatRateCreated = "vat-rate-created";
    public const string VatRateUpdated = "vat-rate-updated";
    public const string UserCreated = "user-created";
    public const string UserUpdated = "user-updated";
    public const string UserDeleted = "user-deleted";
    public const string PasswordChanged = "password-changed";
    public const string UserUnlocked = "user-unlocked";
    public const string SettingsChanged = "settings-changed";

    public static string Finnish(string action) => action switch
    {
        SignIn => "Kirjautuminen",
        SignInFailed => "Epäonnistunut kirjautuminen",
        SignOut => "Uloskirjautuminen",
        SaleVoided => "Myynnin mitätöinti",
        ProductCreated => "Tuote lisätty",
        ProductUpdated => "Tuotetta muutettu",
        ProductDeleted => "Tuote poistettu",
        VatRateCreated => "ALV-kanta lisätty",
        VatRateUpdated => "ALV-kantaa muutettu",
        UserCreated => "Käyttäjä lisätty",
        UserUpdated => "Käyttäjää muutettu",
        UserDeleted => "Käyttäjä poistettu",
        PasswordChanged => "Salasana vaihdettu",
        UserUnlocked => "Tilin lukitus avattu",
        SettingsChanged => "Kassan asetuksia muutettu",
        _ => action,
    };
}
