using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;

namespace MediaCleaner.Compatibility;

internal static class UserManagerCompatibility
{
    public static List<User> GetUsers(object userManager)
    {
        var type = userManager.GetType();
        var getUsers = type.GetMethod("GetUsers", BindingFlags.Instance | BindingFlags.Public);
        if (getUsers?.GetParameters().Length == 0
            && getUsers.Invoke(userManager, null) is IEnumerable<User> modernUsers)
        {
            return modernUsers.ToList();
        }

        var usersProperty = type.GetProperty("Users", BindingFlags.Instance | BindingFlags.Public);
        if (usersProperty?.GetValue(userManager) is IEnumerable<User> legacyUsers)
        {
            return legacyUsers.ToList();
        }

        throw new InvalidOperationException("Unable to enumerate Jellyfin users.");
    }
}
