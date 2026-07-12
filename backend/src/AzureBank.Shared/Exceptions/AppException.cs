namespace AzureBank.Shared.Exceptions;

#pragma warning disable IDE0290 // Use primary constructor - can't use with abstract + protected

public abstract class AppException : Exception
{
    public string ErrorCode { get; set; }
    public int StatusCode { get; }
    public Dictionary<string, object>? Details { get; protected set; }

    protected AppException(string message, string errorCode, int statusCode)
    : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}


// #pragma warning restore IDE0290  // ← NEEDED if more classes follow 

// PRIMARY CONSTRUCTOR VERSION - NOT USED
// Reason: Can't use 'abstract' or 'protected' with primary constructors.
// This allows direct instantiation (throw new AppException(...)) which we want to prevent.
// We need 'abstract' to force use of specific exceptions like NotFoundException.

//namespace AzureBank.Shared.Exceptions;


//public class AppException(string message, string errorCode, int statusCode) : Exception(message)
//{
//    public string ErrorCode { get; set; } = errorCode;
//    public int StatusCode { get; } = statusCode;
//    public Dictionary<string, object>? Details { get; protected set; }
//}


// NOT USED: Primary constructors don't support 'abstract' + 'protected constructor'.
// This would allow: throw new AppException(...) - we want to force specific exceptions.
