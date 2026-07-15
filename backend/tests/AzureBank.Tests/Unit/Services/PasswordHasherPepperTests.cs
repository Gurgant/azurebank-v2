using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Implementations;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for the PIN-hash pepper (ADR-0011): PIN hashing/verification mixes in
/// a server-side secret (Argon2id KnownSecret) that is kept out of the database, and
/// new PIN hashes are tagged with a self-describing "keyid" so hashes can be verified
/// with the right pepper and migrated on use (rehash-on-use).
/// </summary>
public class PasswordHasherPepperTests
{
    private const string PepperA = "unit-tests-pin-pepper-A-0123456789abcdef0123456789";
    private const string PepperB = "unit-tests-pin-pepper-B-9876543210fedcba9876543210";
    private const string PepperC = "unit-tests-pin-pepper-C-abcdefabcdefabcdefabcdef01";

    private static PasswordHasher Peppered(string pepper = PepperA, int keyId = 1)
        => new(new PinHashingOptions { PinPepper = pepper, PinPepperKeyId = keyId });

    private static PasswordHasher Rotated(string activePepper, int activeKeyId, Dictionary<int, string> previous)
        => new(new PinHashingOptions
        {
            PinPepper = activePepper,
            PinPepperKeyId = activeKeyId,
            PreviousPinPeppers = previous,
        });

    private static PasswordHasher Legacy() => new(); // no pepper configured

    [Fact]
    public void HashPin_WithPepper_TagsActiveKeyId()
    {
        var hash = Peppered(keyId: 1).HashPin("123456");

        hash.Should().Contain("keyid=1");
        hash.Split('$').Should().HaveCount(6, "the keyid lives inside the PHC parameter block");
    }

    [Fact]
    public void HashPin_WithoutPepper_HasNoKeyId()
    {
        Legacy().HashPin("123456").Should().NotContain("keyid");
    }

    [Fact]
    public void VerifyPin_WithSamePepper_ReturnsTrue()
    {
        var sut = Peppered();
        var hash = sut.HashPin("123456");

        sut.VerifyPin(hash, "123456").Should().BeTrue();
        sut.VerifyPin(hash, "654321").Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithWrongPepper_ReturnsFalse()
    {
        // Same key id, different secret → the DB-only value can't reproduce the hash.
        var hash = Peppered(PepperA, keyId: 1).HashPin("123456");

        Peppered(PepperB, keyId: 1).VerifyPin(hash, "123456").Should().BeFalse(
            "the pepper is required in addition to the stored hash (offline brute-force defense)");
    }

    [Fact]
    public void VerifyPin_PepperedHash_WithUnpepperedHasher_ReturnsFalse()
    {
        // A hasher that holds no pepper cannot verify a hash tagged with a keyid: fail closed.
        var hash = Peppered().HashPin("123456");

        Legacy().VerifyPin(hash, "123456").Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_LegacyHash_WithPepperedHasher_ReturnsTrue()
    {
        // A legacy (un-peppered) hash carries no keyid, so a peppered hasher verifies
        // it WITHOUT applying the pepper — self-describing, no forced reset.
        var legacyHash = Legacy().HashPin("123456");

        Peppered().VerifyPin(legacyHash, "123456").Should().BeTrue();
    }

    [Fact]
    public void PinNeedsRehash_ForLegacyHash_WhenPepperActive_IsTrue()
    {
        var legacyHash = Legacy().HashPin("123456");

        Peppered().PinNeedsRehash(legacyHash).Should().BeTrue();
    }

    [Fact]
    public void PinNeedsRehash_ForCurrentKeyId_IsFalse()
    {
        var sut = Peppered(keyId: 1);
        var hash = sut.HashPin("123456");

        sut.PinNeedsRehash(hash).Should().BeFalse();
    }

    [Fact]
    public void PinNeedsRehash_ForOlderKeyId_IsTrue()
    {
        var oldHash = Peppered(PepperA, keyId: 1).HashPin("123456");

        // The active pepper is now key id 2 → a key-id-1 hash should be re-hashed.
        Peppered(PepperB, keyId: 2).PinNeedsRehash(oldHash).Should().BeTrue();
    }

    [Fact]
    public void PinNeedsRehash_WithoutPepper_IsAlwaysFalse()
    {
        var anyHash = Peppered().HashPin("123456");

        Legacy().PinNeedsRehash(anyHash).Should().BeFalse();
        Legacy().PinNeedsRehash(Legacy().HashPin("123456")).Should().BeFalse();
    }

    [Fact]
    public void LegacyHash_UpgradesToV2_AndKeepsVerifying()
    {
        // Full v1 → v2 migration story on the hasher itself.
        var sut = Peppered(keyId: 1);
        var legacyHash = Legacy().HashPin("123456");

        // v1 verifies and is flagged for rehash.
        sut.VerifyPin(legacyHash, "123456").Should().BeTrue();
        sut.PinNeedsRehash(legacyHash).Should().BeTrue();

        // Re-hash with the active pepper → v2, verifies, no longer needs rehash.
        var v2Hash = sut.HashPin("123456");
        v2Hash.Should().Contain("keyid=1");
        sut.VerifyPin(v2Hash, "123456").Should().BeTrue();
        sut.PinNeedsRehash(v2Hash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_AfterRotation_OldKeyIdHash_StillVerifies()
    {
        // A hash minted with pepper A / keyid 1 ...
        var oldHash = Peppered(PepperA, keyId: 1).HashPin("123456");

        // ... after rotating the active pepper to B / keyid 2 while RETAINING A in the ring.
        var rotated = Rotated(PepperB, activeKeyId: 2, previous: new() { [1] = PepperA });

        rotated.VerifyPin(oldHash, "123456").Should().BeTrue(
            "the retired pepper stays in the keyring, so old-keyid hashes keep verifying (zero-downtime rotation)");
        rotated.PinNeedsRehash(oldHash).Should().BeTrue();

        // Rehash-on-use drains it to the active key.
        var upgraded = rotated.HashPin("123456");
        upgraded.Should().Contain("keyid=2");
        rotated.VerifyPin(upgraded, "123456").Should().BeTrue();
        rotated.PinNeedsRehash(upgraded).Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_AfterRotation_WithoutRetainingOldPepper_FailsClosed()
    {
        var oldHash = Peppered(PepperA, keyId: 1).HashPin("123456");

        // Active rotated to keyid 2 but the old pepper was NOT retained in the ring.
        var rotated = Peppered(PepperB, keyId: 2);

        rotated.VerifyPin(oldHash, "123456").Should().BeFalse(
            "dropping a pepper before its hashes are drained bricks them — retire only after drain");
        rotated.PinPepperMissingFor(oldHash).Should().BeTrue();
    }

    [Fact]
    public void PinPepperMissingFor_ReflectsKeyringMembership()
    {
        var hasher = Rotated(PepperB, activeKeyId: 2, previous: new() { [1] = PepperA });

        hasher.PinPepperMissingFor(Peppered(PepperA, keyId: 1).HashPin("123456"))
            .Should().BeFalse("keyid 1 is retained in the ring");
        hasher.PinPepperMissingFor(Peppered(PepperC, keyId: 9).HashPin("123456"))
            .Should().BeTrue("keyid 9 is not in the ring");
        hasher.PinPepperMissingFor(Legacy().HashPin("123456"))
            .Should().BeFalse("a legacy hash carries no keyid");
    }

    [Theory]
    [InlineData(524288)]      // 512 MiB — above the 256 MiB cap (would pass the old 1 GiB bound)
    [InlineData(2000000000)]  // absurd
    public void VerifyPin_WithMemoryAboveCap_ReturnsFalse(int memoryKib)
    {
        // A tampered hash claiming an oversized memory cost must be rejected by the
        // bounds check (valid Base64 salt/hash so it reaches the bounds gate).
        var hash = $"$argon2id$v=19$m={memoryKib},t=2,p=4$c2FsdHNhbHQ=$aGFzaGhhc2g=";
        Peppered().VerifyPin(hash, "123456").Should().BeFalse();
        Legacy().VerifyPin(hash, "123456").Should().BeFalse();
    }

    [Fact]
    public void PinPepperMissingFor_NonArgon2idHash_ReturnsFalse()
    {
        // A 6-segment non-argon2id string with an unheld keyid is an invalid-format
        // concern, NOT a retired-pepper one — must not raise the orphaned-pepper signal.
        const string fake = "$pbkdf2$v=19$m=1,t=1,p=1,keyid=9$c2FsdA==$aGFzaA==";
        Rotated(PepperB, activeKeyId: 2, previous: new() { [1] = PepperA })
            .PinPepperMissingFor(fake).Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithNullPreviousPinPeppers_DoesNotThrow_AndWorks()
    {
        var sut = new PasswordHasher(new PinHashingOptions
        {
            PinPepper = PepperA,
            PinPepperKeyId = 1,
            PreviousPinPeppers = null!,
        });

        var hash = sut.HashPin("123456");
        hash.Should().Contain("keyid=1");
        sut.VerifyPin(hash, "123456").Should().BeTrue();
    }

    [Theory]
    [InlineData("$argon2id$v=19$t=2,p=4$c2FsdHNhbHQ=$aGFzaGhhc2g=")]        // missing m
    [InlineData("$argon2id$v=19$m=abc,t=2,p=4$c2FsdHNhbHQ=$aGFzaGhhc2g=")]  // non-numeric m
    [InlineData("$argon2id$v=19$m=19456,p=4$c2FsdHNhbHQ=$aGFzaGhhc2g=")]    // missing t
    public void VerifyPin_WithMalformedCostParameters_ReturnsFalse(string hash)
    {
        // Parsed without throwing: a missing/non-numeric m/t/p is just an invalid hash.
        Peppered().VerifyPin(hash, "123456").Should().BeFalse();
        Legacy().VerifyPin(hash, "123456").Should().BeFalse();
    }

    [Fact]
    public void VerifyPin_WithMalformedKeyId_ReturnsFalse()
    {
        const string hash = "$argon2id$v=19$m=19456,t=2,p=4,keyid=xyz$c2FsdHNhbHQ=$aGFzaGhhc2g=";
        Peppered().VerifyPin(hash, "123456").Should().BeFalse("a non-numeric keyid is an invalid hash");
    }

    [Theory]
    [InlineData("not-a-valid-hash")]
    [InlineData("")]
    [InlineData("$pbkdf2$v=19$m=1,t=1,p=1$c2FsdA==$aGFzaA==")] // 6 segments but wrong algorithm id
    public void PinNeedsRehash_WithMalformedHash_ReturnsFalse(string hash)
    {
        // A malformed / non-argon2id string must not be flagged as needing a rehash.
        Peppered().PinNeedsRehash(hash).Should().BeFalse();
    }

    [Fact]
    public void Hasher_IsThreadSafe_WhenShared()
    {
        // The hasher is registered as a singleton, so a single instance is shared
        // across all concurrent requests. It must be safe to hash/verify in parallel
        // (immutable after construction: readonly ring, no shared mutable state).
        var sut = Peppered();
        var hash = sut.HashPin("123456");

        Parallel.For(0, 32, _ =>
        {
            sut.HashPin("123456").Should().Contain("keyid=1");
            sut.VerifyPin(hash, "123456").Should().BeTrue();
            sut.VerifyPin(hash, "000000").Should().BeFalse();
        });
    }

    [Fact]
    public void Password_IsNeverPeppered_EvenOnAPepperedHasher()
    {
        var sut = Peppered();

        // Account passwords stay with Identity; the password profile is not peppered.
        var hash = sut.HashPassword("SecurePass123!");
        hash.Should().NotContain("keyid");
        sut.VerifyPassword(hash, "SecurePass123!").Should().BeTrue();
    }
}
