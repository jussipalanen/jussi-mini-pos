namespace JussiMiniPos.Models;

/// <summary>
/// The Finnish business ID (Y-tunnus) printed on receipts: seven digits, a
/// hyphen and a check digit, such as 1572860-0.
/// </summary>
public static class BusinessId
{
    private static readonly int[] Weights = [7, 9, 10, 5, 8, 4, 2];

    /// <summary>
    /// Null when <paramref name="text"/> is a valid business ID or empty,
    /// otherwise the Finnish reason it is not. An ID registered before 2003
    /// may have six digits and is written with a leading zero.
    /// </summary>
    public static string? Validate(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length != 9 || text[7] != '-' || !text.Remove(7, 1).All(char.IsAsciiDigit))
        {
            return "Y-tunnuksen muoto on 1234567-8.";
        }

        var remainder = Weights.Select((weight, i) => weight * (text[i] - '0')).Sum() % 11;
        var check = remainder switch
        {
            0 => 0,
            1 => -1,
            _ => 11 - remainder,
        };

        return check == text[8] - '0' ? null : "Y-tunnuksen tarkistusnumero ei täsmää.";
    }
}
