// File: web/OnlineStoreSystem.Storefront/Clients/ApiEndpointsOptions.cs
using System.ComponentModel.DataAnnotations;

namespace OnlineStoreSystem.Storefront.Clients;

/// <summary>Base URLs of the back-end APIs (section <c>Apis</c>).</summary>
public sealed class ApiEndpointsOptions
{
    public const string SectionName = "Apis";

    [Required]
    [Url]
    public string CatalogBaseUrl { get; set; } = "http://localhost:5001";

    [Required]
    [Url]
    public string OrderBaseUrl { get; set; } = "http://localhost:5002";

    [Required]
    [Url]
    public string InventoryBaseUrl { get; set; } = "http://localhost:5003";

    public static Uri ToBaseUri(string url) => new(url.EndsWith('/') ? url : url + "/");
}
