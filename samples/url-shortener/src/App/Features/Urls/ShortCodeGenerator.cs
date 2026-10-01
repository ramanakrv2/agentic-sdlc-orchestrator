using System.Security.Cryptography;

namespace App.Features.Urls;

public interface IShortCodeGenerator
{
    string Generate();
}

/// <summary>
/// Random base62 codes (62^7, about 3.5 trillion). Random rather than a sequential counter so that many instances can
/// generate codes without coordination and codes are not enumerable; collisions are handled by the unique index + retry.
/// </summary>
public sealed class Base62CodeGenerator : IShortCodeGenerator
{
    public const int Length = 7;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}
