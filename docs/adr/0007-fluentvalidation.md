# ADR-0007: FluentValidation for Request Validation

**Status**: Accepted

**Date**: 2026-01-11

**Decision Makers**: Architecture Team

---

## Context

API request validation is critical for:
- Security (preventing injection attacks)
- Data integrity (ensuring valid data reaches the database)
- User experience (providing clear error messages)
- Business rule enforcement (validating domain constraints)

## Decision Drivers

- **Expressiveness**: Complex validation rules should be readable
- **Testability**: Validators should be unit-testable in isolation
- **Separation of Concerns**: Validation logic separate from DTOs
- **Flexibility**: Support for conditional and cross-property validation
- **Integration**: Seamless ASP.NET Core integration

## Considered Options

1. **FluentValidation**: Fluent builder-based validation library
2. **Data Annotations**: Built-in attribute-based validation
3. **Custom Validation**: Hand-written validation code
4. **MiniValidation**: Lightweight reflection-based validation

## Decision

Use **FluentValidation** (`FluentValidation.DependencyInjectionExtensions` v11.11.0) for all request validation.

```csharp
public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Invalid email format")
            .MaximumLength(255);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required")
            .MinimumLength(8)
            .MaximumLength(128);
    }
}
```

## Rationale

### Why FluentValidation?

1. **Readable Rules**: Fluent syntax reads like English
2. **Powerful Features**: Conditional, collection, and async validation
3. **Clean DTOs**: No attributes cluttering model classes
4. **Testable**: Each validator can be unit tested
5. **Extensible**: Custom rules and validators supported

### Data Annotations vs FluentValidation

| Aspect | Data Annotations | FluentValidation |
|--------|-----------------|------------------|
| Syntax | Attributes on properties | Fluent builder methods |
| Complexity | Simple rules only | Any complexity |
| Conditional | ❌ Not supported | ✅ `When()`, `Unless()` |
| Cross-property | ❌ Limited | ✅ Full support |
| Testability | ⚠️ Requires model state | ✅ Direct validation |
| Error messages | ⚠️ Per-attribute | ✅ Fully customizable |
| Localization | ⚠️ Complex | ✅ Built-in support |

### Example: Data Annotations Limitation

Data Annotations cannot express:

```csharp
// "Password confirmation must match password"
// "Amount must be less than account balance"
// "Start date must be before end date"
```

FluentValidation handles these easily:

```csharp
RuleFor(x => x.ConfirmPassword)
    .Equal(x => x.Password)
    .WithMessage("Passwords must match");

RuleFor(x => x.StartDate)
    .LessThan(x => x.EndDate)
    .When(x => x.EndDate.HasValue);
```

## Consequences

### Positive

- Complex validation logic is readable and maintainable
- Validators are easily unit tested
- DTOs remain clean (POCO style)
- Rich error messages improve UX
- Automatic ASP.NET Core integration

### Negative

- Additional NuGet dependency
- Learning curve for fluent syntax
- Validation logic separate from models (can be a pro or con)

### Neutral

- Validators must be registered in DI
- Manual validation possible if needed

## Implementation

### Validator Registration

```csharp
// Program.cs or ServiceCollectionExtensions.cs
services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();
```

### Validator Structure

```
AzureBank.Api/
└── Validators/
    ├── Auth/
    │   ├── LoginRequestValidator.cs
    │   ├── RegisterRequestValidator.cs
    │   ├── SetPinRequestValidator.cs
    │   └── VerifyPinRequestValidator.cs
    ├── Account/
    │   ├── CreateAccountRequestValidator.cs
    │   └── UpdateAccountRequestValidator.cs
    ├── Transaction/
    │   ├── DepositRequestValidator.cs
    │   └── WithdrawRequestValidator.cs
    └── Transfer/
        ├── TransferRequestValidator.cs
        └── InternalTransferRequestValidator.cs
```

### Example Validators

#### Password Validation
```csharp
public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain uppercase letter")
            .Matches("[a-z]").WithMessage("Password must contain lowercase letter")
            .Matches("[0-9]").WithMessage("Password must contain digit")
            .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain special character");

        RuleFor(x => x.AzureTag)
            .NotEmpty()
            .MinimumLength(3)
            .MaximumLength(20)
            .Matches("^[a-z][a-z0-9]*$")
            .WithMessage("AzureTag must start with letter and contain only lowercase letters/numbers");
    }
}
```

#### Money Validation
```csharp
public class DepositRequestValidator : AbstractValidator<DepositRequest>
{
    public DepositRequestValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty();

        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .LessThanOrEqualTo(1_000_000_000)
            .PrecisionScale(18, 2, ignoreTrailingZeros: true);
    }
}
```

### Error Response Format

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "errors": {
    "Email": ["Email is required", "Invalid email format"],
    "Password": ["Password must contain uppercase letter"]
  }
}
```

### Unit Testing Validators

```csharp
public class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    [Fact]
    public void Validate_ValidRequest_ShouldPass()
    {
        var request = new LoginRequest
        {
            Email = "test@example.com",
            Password = "ValidPass123!"
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-email")]
    [InlineData("@example.com")]
    public void Validate_InvalidEmail_ShouldFail(string email)
    {
        var request = new LoginRequest
        {
            Email = email,
            Password = "ValidPass123!"
        };

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }
}
```

## Validation Rules Summary

| Field | Rules |
|-------|-------|
| **Email** | Required, valid format, max 255 |
| **Password** | Required, 8-128 chars, uppercase, lowercase, digit, special |
| **PIN** | Exactly 6 digits |
| **AzureTag** | 3-20 chars, lowercase, starts with letter |
| **Account Name** | Required, 2-100 chars |
| **Amount** | Positive, max 2 decimals, max 1 billion |
| **Account ID** | Required, non-empty GUID |

## Related

- [ADR-0006: Mapperly Object Mapping](./0006-mapperly-object-mapping.md)
- [AzureBank.Api README](../../src/AzureBank.Api/README.md)

---

## References

- [FluentValidation Documentation](https://docs.fluentvalidation.net/)
- [FluentValidation GitHub](https://github.com/FluentValidation/FluentValidation)
- [ASP.NET Core Integration](https://docs.fluentvalidation.net/en/latest/aspnet.html)
