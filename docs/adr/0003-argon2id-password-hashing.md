# ADR-0003: Argon2id Password Hashing

**Status**: Accepted

**Date**: 2026-01-12

**Decision Makers**: Vladislav Aleshaev

---

## Context

User passwords must be securely hashed before storage. The hashing algorithm must be:
- Resistant to brute-force attacks
- Resistant to GPU/ASIC acceleration
- OWASP recommended
- Memory-hard to increase attack cost

## Decision Drivers

- **Security**: Must use current best practices
- **OWASP Compliance**: Follow OWASP password storage guidelines
- **Performance**: Reasonable login latency (< 500ms)
- **Future-Proof**: Algorithm should remain secure for years

## Considered Options

1. **Argon2id**: Winner of Password Hashing Competition (2015)
2. **bcrypt**: Industry standard, widely used
3. **PBKDF2**: Built into ASP.NET Core Identity
4. **scrypt**: Memory-hard, used by some cryptocurrencies

## Decision

Use **Argon2id** with the following parameters:
- Memory: 64 MB
- Iterations: 3
- Parallelism: 4

Implemented via the `Konscious.Security.Cryptography.Argon2` NuGet package (v1.3.1).

## Rationale

### OWASP Recommendation

> "Use Argon2id with a minimum configuration of 19 MiB of memory, an iteration count of 2, and 1 degree of parallelism."
> — [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)

### Algorithm Comparison

| Algorithm | Memory-Hard | GPU Resistant | OWASP Rank | Year |
|-----------|-------------|---------------|------------|------|
| Argon2id | ✅ Yes | ✅ Excellent | 1st | 2015 |
| bcrypt | ❌ No | ⚠️ Moderate | 2nd | 1999 |
| scrypt | ✅ Yes | ✅ Good | 3rd | 2009 |
| PBKDF2 | ❌ No | ❌ Poor | 4th | 2000 |

### Why Argon2id over bcrypt?

1. **Memory-Hard**: Requires significant RAM, making GPU attacks expensive
2. **Modern Design**: Won Password Hashing Competition in 2015
3. **Hybrid Mode**: Argon2id combines Argon2i (side-channel resistant) and Argon2d (GPU resistant)
4. **Configurable**: Memory, time, and parallelism parameters
5. **OWASP #1**: First choice in OWASP recommendations

### Why Not ASP.NET Core Identity Default?

ASP.NET Core Identity uses PBKDF2 by default, which:
- Is not memory-hard
- Is vulnerable to GPU acceleration
- Is ranked 4th by OWASP

## Consequences

### Positive

- Best-in-class password security
- OWASP compliant
- Resistant to GPU/ASIC attacks
- Configurable security/performance tradeoff
- Forward-compatible (can increase parameters)

### Negative

- Not built into ASP.NET Core Identity (custom implementation)
- Requires external NuGet package
- Higher memory usage per hash operation
- Slower than PBKDF2 (intentional, security feature)

### Neutral

- Need to implement custom `IPasswordHasher<ApplicationUser>`
- Password verification takes ~200-500ms (acceptable for login)

## Implementation

```csharp
public class Argon2PasswordHasher : IPasswordHasher<ApplicationUser>
{
    private const int MemorySize = 65536; // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 4;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    public string HashPassword(ApplicationUser user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemorySize,
            Iterations = Iterations,
            DegreeOfParallelism = Parallelism
        };

        var hash = argon2.GetBytes(HashLength);
        return Convert.ToBase64String(salt.Concat(hash).ToArray());
    }
}
```

## Validation

Success criteria:
- All password hashes use Argon2id
- Hash verification completes < 500ms
- Memory usage during hashing as expected
- No timing attacks possible

## Related

- [ADR-0001: BFF Pattern](./0001-bff-pattern.md)
- [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [Argon2 RFC](https://datatracker.ietf.org/doc/rfc9106/)

---

## References

- [Password Hashing Competition](https://password-hashing.net/)
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
