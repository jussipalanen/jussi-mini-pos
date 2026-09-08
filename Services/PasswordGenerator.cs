using System;
using System.Security.Cryptography;

namespace JussiMiniPos.Services;

/// <summary>
/// Makes up a password when nobody supplied one.
///
/// This exists so no password is ever written into the source. A constant
/// default would be the same on every install and in every copy of the
/// repository, which makes it a published credential rather than a secret;
/// a generated one is different everywhere and is shown once to whoever it
/// belongs to.
/// </summary>
public static class PasswordGenerator
{
    /// <summary>
    /// Characters that cannot be mistaken for each other when read off a
    /// screen and typed in again: no O/0, no I/l/1, no 5/S, no 2/Z.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRTUVWXYabcdefghijkmnopqrtuvwxy346789";

    private const int Groups = 4;

    private const int GroupLength = 4;

    /// <summary>
    /// A password like <c>Kfx7-Rm9t-Qbv4-Xhn6</c>. Sixteen characters from a
    /// 50-character alphabet is a little over 90 bits, which is far past
    /// anything worth guessing, and the dashes are there so it can be read
    /// aloud and typed without mistakes.
    /// </summary>
    public static string Generate()
    {
        var groups = new string[Groups];

        for (var i = 0; i < Groups; i++)
        {
            // Cryptographically random, and uniform over the alphabet rather
            // than biased by a modulo.
            groups[i] = RandomNumberGenerator.GetString(Alphabet, GroupLength);
        }

        return string.Join('-', groups);
    }
}
