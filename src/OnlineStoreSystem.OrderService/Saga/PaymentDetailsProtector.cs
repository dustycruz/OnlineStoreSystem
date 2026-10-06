// File: src/OnlineStoreSystem.OrderService/Saga/PaymentDetailsProtector.cs
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OnlineStoreSystem.Shared.Contracts.Orders;
using OnlineStoreSystem.Shared.Validation;

namespace OnlineStoreSystem.OrderService.Saga;

/// <summary>Card data the saga needs between "Create Order" and "Process Payment".</summary>
public sealed record SagaCardData(string CardNumber, string CardHolderName, int ExpiryMonth, int ExpiryYear)
{
    public string Last4 => CardNumber[^4..];
}

/// <summary>
/// The payment step runs asynchronously (after InventoryService replied), so the card details must survive
/// between the checkout request and that moment - even a process restart. They are stored in
/// <c>SagaStates.ProtectedPaymentDetails</c> ENCRYPTED with ASP.NET Core Data Protection (AES-256-CBC +
/// HMACSHA256, rotating keys) and wiped as soon as the payment step finished.
/// </summary>
public sealed class PaymentDetailsProtector(IDataProtectionProvider dataProtectionProvider, ILogger<PaymentDetailsProtector> logger)
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("OnlineStoreSystem.OrderService.Saga.PaymentDetails.v1");

    public string Protect(PaymentDetailsRequest payment)
    {
        var data = new SagaCardData(
            LuhnCardNumberAttribute.Normalize(payment.CardNumber),
            payment.CardHolderName.Trim(),
            payment.ExpiryMonth,
            payment.ExpiryYear);

        return _protector.Protect(JsonSerializer.Serialize(data));
    }

    public SagaCardData? TryUnprotect(string? protectedPayload)
    {
        if (string.IsNullOrEmpty(protectedPayload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SagaCardData>(_protector.Unprotect(protectedPayload));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            logger.LogError(ex, "Stored payment details could not be decrypted (key ring changed?)");
            return null;
        }
    }

    public static string Last4(PaymentDetailsRequest payment) =>
        LuhnCardNumberAttribute.Normalize(payment.CardNumber)[^4..];
}
