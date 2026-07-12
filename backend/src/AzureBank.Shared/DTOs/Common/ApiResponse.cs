namespace AzureBank.Shared.DTOs.Common;

public class ApiResponse<T>
{
    public T? Data { get; set; }
    public string? Message { get; set; }

    public static ApiResponse<T> Success(T data, string? message = null)
    {
        return new ApiResponse<T>
        {
            Data = data,
            Message = message
        };
    }
}

public class ApiResponse
{
    public string? Message { get; set; }
    public static ApiResponse Success(string? message = null)
    {
        return new ApiResponse
        {
            Message = message
        };
    }

}
