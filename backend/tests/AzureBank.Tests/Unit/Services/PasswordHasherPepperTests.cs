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

    [Fact]
    public void VerifyPin_WithAbsurdCostParameters_ReturnsFalse()
    {
        // A tampered hash claiming a ~2 GiB memory cost must be rejected by the bounds
        // check, not acted upon (valid Base64 salt/hash so it reaches the bounds gate).
        const string absurd = "$argon2id$v=19$m=2000000000,t=2,p=4$c2FsdHNhbHQ=$aGFzaGhhc2g=";
        Peppered().VerifyPin(absurd, "123456").Should().BeFalse();
        Legacy().VerifyPin(absurd, "123456").Should().BeFalse();
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
