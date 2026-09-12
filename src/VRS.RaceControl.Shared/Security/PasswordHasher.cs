using System.Security.Cryptography;

namespace VRS.RaceControl.Shared.Security;

/// <summary>
/// PBKDF2-SHA256 password hashing with a per-account cryptographic salt.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public static PasswordHashResult Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (iterations < 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashLength);

        return new PasswordHashResult(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            iterations);
    }

    public static bool Verify(string password, string encodedHash, string encodedSalt, int iterations)
    {
        if (string.IsNullOrEmpty(password)
            || string.IsNullOrEmpty(encodedHash)
            || string.IsNullOrEmpty(encodedSalt)
            || iterations < 100_000)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(encodedHash);
            var salt = Convert.FromBase64String(encodedSalt);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public readonly record struct PasswordHashResult(string Hash, string Salt, int Iterations);
