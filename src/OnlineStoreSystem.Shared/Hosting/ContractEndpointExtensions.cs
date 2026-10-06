// File: src/OnlineStoreSystem.Shared/Hosting/ContractEndpointExtensions.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace OnlineStoreSystem.Shared.Hosting;

/// <summary>
/// Contract-first support: serves the hand-written OpenAPI YAML files (copied from <c>/contracts</c>
/// into the build output by each service's .csproj) at <c>/contracts/{file}</c>, so Swagger UI can render
/// the authoritative contract next to the document generated from code.
/// </summary>
public static class ContractEndpointExtensions
{
    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapApiContracts()
        {
            var contractsDirectory = Path.Combine(AppContext.BaseDirectory, "contracts");

            endpoints.MapGet("/contracts/{fileName}", (string fileName) =>
                {
                    // Path.GetFileName strips any directory part => no path traversal.
                    var safeName = Path.GetFileName(fileName);
                    var fullPath = Path.Combine(contractsDirectory, safeName);
                    var isYaml = safeName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                 || safeName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

                    return isYaml && File.Exists(fullPath)
                        ? Results.File(fullPath, "application/yaml; charset=utf-8")
                        : Results.Problem(
                            statusCode: StatusCodes.Status404NotFound,
                            title: "Contract not found",
                            detail: $"No OpenAPI contract named '{safeName}' is published by this service.");
                })
                .ExcludeFromDescription();

            return endpoints;
        }
    }
}
