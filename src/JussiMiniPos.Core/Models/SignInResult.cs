using System;

namespace JussiMiniPos.Models;

/// <summary>How a sign-in attempt ended.</summary>
public enum SignInStatus
{
    Success,

    /// <summary>Unknown username or wrong password — deliberately one answer for both.</summary>
    WrongCredentials,

    /// <summary>Too many failed attempts; the account refuses sign-ins until <see cref="SignInResult.LockedUntil"/>.</summary>
    LockedOut,
}

/// <summary>The outcome of a sign-in attempt, with the user when it succeeded.</summary>
public sealed record SignInResult(SignInStatus Status, User? User = null, DateTimeOffset? LockedUntil = null);
