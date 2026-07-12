namespace AzureBank.Shared.DTOs.Common;

public class PaginatedResponse<T>
{
    public List<T> Data { get; set; } = [];
    public PaginationMetadata Pagination { get; set; } = new();
}

public class PaginationMetadata
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }

    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}
