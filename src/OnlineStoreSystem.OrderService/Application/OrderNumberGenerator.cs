// File: src/OnlineStoreSystem.OrderService/Application/OrderNumberGenerator.cs
using System.Security.Cryptography;

namespace OnlineStoreSystem.OrderService.Application;

/// <summary>Human-friendly order numbers such as <c>ORD-20261006-7KQ2M9XB</c> (unique index UX_Orders_OrderNumber).</summary>
public static class OrderNumberGenerator
{
    // Crockford-like alphabet without 0/O/1/I to avoid transcription mistakes.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Next(DateTime nowUtc)
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);

        Span<char> suffix = stackalloc char[8];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = Alphabet[random[i] % Alphabet.Length];
        }

        return $"ORD-{nowUtc:yyyyMMdd}-{new string(suffix)}";
    }
}
