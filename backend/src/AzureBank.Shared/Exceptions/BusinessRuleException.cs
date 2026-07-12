namespace AzureBank.Shared.Exceptions;

// PRIMARY CONSTRUCTOR VERSION

/// <summary>
/// Exception for business rule violations.
/// Returns HTTP 422 Unprocessable Entity (semantic error, not syntactic).
///
/// Use 400 Bad Request for: malformed JSON, missing required fields, invalid formats
/// Use 422 Unprocessable Entity for: business rules that cannot be expressed in schema
///
/// Examples:
/// - Cannot transfer to the same account (cross-field constraint)
/// - Insufficient funds (domain logic)
/// - Cannot delete primary account (business rule)
///
/// Reference: project-docs/30-business-rule-validation-implementation-plan.md
/// </summary>
public class BusinessRuleException(string message, string errorCode = "BUSINESS_RULE_VIOLATION")
    : AppException(message, errorCode, 422)  // 422 Unprocessable Entity (was 400)
{
}
