// File: src/OnlineStoreSystem.OrderService/Clients/DownstreamServicesOptions.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.OrderService.Clients;

/// <summary>Base URLs of the services OrderService calls synchronously (section <c>Services</c>).</summary>
public sealed class DownstreamServicesOptions
{
    public const string SectionName = "Services";

    [Required]
    [Url]
    public string CatalogBaseUrl { get; set; } = "http://localhost:5001";

    [Required]
    [Url]
    public string PaymentBaseUrl { get; set; } = "http://localhost:5004";
}
