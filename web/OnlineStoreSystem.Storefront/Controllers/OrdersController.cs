// File: web/OnlineStoreSystem.Storefront/Controllers/OrdersController.cs
using Microsoft.AspNetCore.Mvc;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Shared.DTOs;
using OnlineStoreSystem.Storefront.Clients;
using OnlineStoreSystem.Storefront.Models;
using OnlineStoreSystem.Storefront.Services;

namespace OnlineStoreSystem.Storefront.Controllers;

/// <summary>Cart, checkout (starts the saga in OrderService) and order status pages.</summary>
public sealed class OrdersController(
    StorefrontApiClient api,
    CartService cart,
    CustomerContext customer,
    ILogger<OrdersController> logger) : Controller
{
    // ------------------------------------------------------------------ cart

    [HttpGet]
    public IActionResult Cart() => View(new CartViewModel { Lines = cart.GetLines() });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToCart(int productId, int quantity = 1, string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        var product = await api.GetProductAsync(productId, cancellationToken);
        if (!product.IsSuccess)
        {
            TempData["Error"] = product.ErrorMessage ?? $"Product {productId} is not available.";
            return RedirectBack(returnUrl);
        }

        if (!product.Value!.IsActive)
        {
            TempData["Error"] = $"{product.Value.Name} is no longer sold.";
            return RedirectBack(returnUrl);
        }

        cart.Add(product.Value, Math.Clamp(quantity, 1, CartService.MaxQuantityPerLine));
        TempData["Flash"] = $"Added {quantity} x {product.Value.Name} to your cart.";
        return RedirectBack(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateCart(int productId, int quantity)
    {
        cart.SetQuantity(productId, quantity);
        return RedirectToAction(nameof(Cart));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveFromCart(int productId)
    {
        cart.Remove(productId);
        TempData["Flash"] = "Item removed from your cart.";
        return RedirectToAction(nameof(Cart));
    }

    // -------------------------------------------------------------- checkout

    [HttpGet]
    public IActionResult Checkout()
    {
        var lines = cart.GetLines();
        if (lines.Count == 0)
        {
            TempData["Error"] = "Your cart is empty.";
            return RedirectToAction(nameof(Cart));
        }

        return View(new CheckoutViewModel
        {
            CardHolderName = customer.LastName ?? string.Empty,
            Email = customer.LastEmail ?? string.Empty,
            Cart = new CartViewModel { Lines = lines }
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(CheckoutViewModel model, CancellationToken cancellationToken)
    {
        var lines = cart.GetLines();
        model.Cart = new CartViewModel { Lines = lines };
        if (lines.Count == 0)
        {
            TempData["Error"] = "Your cart is empty.";
            return RedirectToAction(nameof(Cart));
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var request = new CheckoutRequest
        {
            CustomerId = customer.CustomerId,
            CustomerEmail = model.Email.Trim(),
            Items = lines.Select(l => new OrderLineRequest { ProductId = l.ProductId, Quantity = l.Quantity }).ToList(),
            Payment = new PaymentDetailsRequest
            {
                CardNumber = model.CardNumber,
                CardHolderName = model.CardHolderName.Trim(),
                ExpiryMonth = model.ExpiryMonth,
                ExpiryYear = model.ExpiryYear
            }
        };

        var result = await api.CheckoutAsync(request, cancellationToken);
        if (result.IsSuccess)
        {
            cart.Clear();
            customer.Remember(model.CardHolderName.Trim(), model.Email.Trim());
            logger.LogInformation("Checkout accepted: order {OrderNumber} ({OrderId}) is {Status}",
                result.Value!.OrderNumber, result.Value.OrderId, result.Value.Status);
            return RedirectToAction(nameof(Status), new { id = result.Value.OrderId });
        }

        // Map RFC 7807 validation errors from the API onto the form.
        if (result.Problem?.Errors is { Count: > 0 } errors)
        {
            foreach (var (key, messages) in errors)
            {
                foreach (var message in messages)
                {
                    ModelState.AddModelError(MapErrorKey(key), message);
                }
            }
        }
        else
        {
            var reference = result.ProblemCorrelationId ?? HttpContext.CorrelationId;
            ModelState.AddModelError(string.Empty,
                $"{result.ErrorMessage} (HTTP {result.StatusCode?.ToString() ?? "n/a"}, correlation id {reference})");
        }

        return View(model);
    }

    // ---------------------------------------------------------------- status

    [HttpGet]
    public async Task<IActionResult> Status(Guid id, CancellationToken cancellationToken)
    {
        var result = await api.GetOrderAsync(id, cancellationToken);
        if (result.StatusCode == StatusCodes.Status404NotFound)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound", $"Order {id} does not exist.");
        }

        if (!result.IsSuccess)
        {
            return RedirectToAction("Error", "Home", new { message = result.ErrorMessage });
        }

        return View(new OrderStatusViewModel { Order = result.Value! });
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var customerId = customer.CustomerId;
        var result = await api.GetOrdersAsync(customerId, cancellationToken);
        return View(new OrdersIndexViewModel
        {
            CustomerId = customerId,
            Orders = result.IsSuccess ? result.Value!.Items : [],
            ErrorMessage = result.IsSuccess ? null : result.ErrorMessage
        });
    }

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Catalog");

    private static string MapErrorKey(string apiKey) => apiKey switch
    {
        _ when apiKey.StartsWith("Payment.CardNumber", StringComparison.OrdinalIgnoreCase) => nameof(CheckoutViewModel.CardNumber),
        _ when apiKey.StartsWith("Payment.CardHolderName", StringComparison.OrdinalIgnoreCase) => nameof(CheckoutViewModel.CardHolderName),
        _ when apiKey.StartsWith("Payment.Expiry", StringComparison.OrdinalIgnoreCase) => nameof(CheckoutViewModel.ExpiryMonth),
        _ when apiKey.Equals("CustomerEmail", StringComparison.OrdinalIgnoreCase) => nameof(CheckoutViewModel.Email),
        _ => string.Empty
    };
}
