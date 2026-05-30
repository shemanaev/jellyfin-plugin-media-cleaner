using Jellyfin.Database.Implementations.Entities;
using MediaCleaner.Compatibility;
using Xunit;

namespace MediaCleaner.Tests.Compatibility;

public class UserManagerCompatibilityTests
{
    [Fact]
    public void Reads_users_from_legacy_users_property()
    {
        var user = new User("legacy", "default", "default");
        var manager = new LegacyUserManager([user]);

        var users = UserManagerCompatibility.GetUsers(manager);

        Assert.Same(user, Assert.Single(users));
    }

    [Fact]
    public void Reads_users_from_modern_get_users_method()
    {
        var user = new User("modern", "default", "default");
        var manager = new ModernUserManager([user]);

        var users = UserManagerCompatibility.GetUsers(manager);

        Assert.Same(user, Assert.Single(users));
    }

    private sealed class LegacyUserManager(IEnumerable<User> users)
    {
        public IEnumerable<User> Users { get; } = users;
    }

    private sealed class ModernUserManager(IEnumerable<User> users)
    {
        public IEnumerable<User> GetUsers() => users;
    }
}
