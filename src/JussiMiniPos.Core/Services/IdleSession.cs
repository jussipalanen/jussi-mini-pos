using System;

namespace JussiMiniPos.Services;

/// <summary>
/// When a signed-in session that nobody is using ends on its own, so a till
/// left signed in on a shared counter does not stay usable by whoever walks
/// up to it. Kept apart from the window so the rule can be tested.
/// </summary>
public static class IdleSession
{
    /// <summary>Time without keyboard, mouse or touch input before the session ends.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when the session should end now. Never in the middle of a card
    /// payment, which the terminal may still be finishing, and never behind
    /// an open dialog, whose unsaved input would be lost; the check simply
    /// comes round again once those are over.
    /// </summary>
    public static bool ShouldSignOut(bool signedIn, TimeSpan idle, bool paymentInProgress, bool dialogOpen) =>
        signedIn && idle >= Timeout && !paymentInProgress && !dialogOpen;
}
