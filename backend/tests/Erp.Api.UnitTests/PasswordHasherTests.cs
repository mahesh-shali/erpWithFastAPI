using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

public class PasswordHasherTests
{
    private readonly BCryptPasswordHasher _sut = new();

    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = _sut.Hash("Admin@123");
        _sut.Verify("Admin@123", hash).Should().BeTrue();
    }

    [Fact]
    public void Wrong_password_fails() => _sut.Verify("nope", _sut.Hash("Admin@123")).Should().BeFalse();

    [Fact]
    public void Same_password_produces_different_hashes() =>
        _sut.Hash("x12345678").Should().NotBe(_sut.Hash("x12345678"));

    [Fact]
    public void Malformed_hash_returns_false_instead_of_throwing() =>
        _sut.Verify("pw", "not-a-bcrypt-hash").Should().BeFalse();

    [Fact]
    public void Verifies_seeded_admin_hash()
    {
        // A known BCrypt hash of the demo admin password (the seeder hashes it at seed time)
        const string seeded = "$2a$11$V76uTc8jRWnA6YTqhH2cveSrOfZ1roFle/sp8tWjHRJOtR/f.qBp.";
        _sut.Verify("Admin@123", seeded).Should().BeTrue();
    }
}
