using System.Security.Cryptography;
using JussiMiniPos.Services;

namespace JussiMiniPos.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void A_hash_is_self_describing_and_at_full_strength()
    {
        var parts = PasswordHasher.Hash("Oikea-salasana-123").Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);

        // Guards against the work factor being lowered by accident.
        Assert.True(int.Parse(parts[1]) >= 600_000, $"Iterations dropped to {parts[1]}.");
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time_and_still_verifies()
    {
        var first = PasswordHasher.Hash("Oikea-salasana-123");
        var second = PasswordHasher.Hash("Oikea-salasana-123");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("Oikea-salasana-123", first));
        Assert.True(PasswordHasher.Verify("Oikea-salasana-123", second));
        Assert.False(PasswordHasher.Verify("oikea-salasana-123", first));
    }

    /// <summary>
    /// Hashes written with an older iteration count keep working after the
    /// count is raised, because the count travels with the hash.
    /// </summary>
    [Fact]
    public void A_hash_is_verified_with_the_iteration_count_it_records()
    {
        var hash = HashWith("vanha-salasana", iterations: 1_000);

        Assert.True(PasswordHasher.Verify("vanha-salasana", hash));
        Assert.False(PasswordHasher.Verify("väärä", hash));
    }

    /// <summary>A damaged row refuses the login instead of throwing or, worse, accepting anything.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("salasana")]
    [InlineData("md5$1000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$abc$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$0$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$not base64!$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$c2FsdA==$")]
    [InlineData("pbkdf2-sha256$1000$c2FsdA==$aGFzaA==$extra")]
    public void A_malformed_hash_fails_closed(string? stored)
    {
        Assert.False(PasswordHasher.Verify("mikä-tahansa", stored));
        Assert.False(PasswordHasher.Verify("", stored));
    }

    private static string HashWith(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}
