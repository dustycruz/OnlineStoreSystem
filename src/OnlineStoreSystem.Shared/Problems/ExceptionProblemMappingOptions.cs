// File: src/OnlineStoreSystem.Shared/Problems/ExceptionProblemMappingOptions.cs
namespace OnlineStoreSystem.Shared.Problems;

/// <summary>
/// Lets each service map its own exception types (e.g. EF Core's <c>DbUpdateConcurrencyException</c>)
/// to problem responses without the Shared library depending on those libraries.
/// </summary>
public sealed class ExceptionProblemMappingOptions
{
    private readonly List<ExceptionProblemMapping> _mappings = [];

    public IReadOnlyList<ExceptionProblemMapping> Mappings => _mappings;

    public ExceptionProblemMappingOptions Map<TException>(int statusCode, string type, string title, bool exposeMessage = false)
        where TException : Exception
    {
        _mappings.Add(new ExceptionProblemMapping(typeof(TException), statusCode, type, title, exposeMessage));
        return this;
    }

    public ExceptionProblemMapping? Find(Exception exception) =>
        _mappings.FirstOrDefault(m => m.ExceptionType.IsInstanceOfType(exception));
}

public sealed record ExceptionProblemMapping(Type ExceptionType, int StatusCode, string Type, string Title, bool ExposeMessage);
