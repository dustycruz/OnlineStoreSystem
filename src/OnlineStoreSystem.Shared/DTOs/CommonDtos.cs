// File: src/OnlineStoreSystem.Shared/DTOs/CommonDtos.cs
namespace OnlineStoreSystem.Shared.DTOs;

/// <summary>Standard page envelope returned by list endpoints.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}
