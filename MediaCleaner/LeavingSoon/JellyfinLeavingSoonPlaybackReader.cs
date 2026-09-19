using System;
using MediaBrowser.Controller.Library;

namespace MediaCleaner.LeavingSoon;

internal sealed class JellyfinLeavingSoonPlaybackReader(
    IUserManager userManager,
    ILibraryManager libraryManager,
    IUserDataManager userDataManager) : ILeavingSoonPlaybackReader
{
    public ProtectionPlaybackObservation Read(string itemId, string userId)
    {
        if (!Guid.TryParse(userId, out var parsedUserId) || userManager.GetUserById(parsedUserId) is not JellyfinUser user)
        {
            return new ProtectionPlaybackObservation(ProtectionPlaybackStatus.UnknownUser);
        }

        if (!Guid.TryParse(itemId, out var parsedItemId) || libraryManager.GetItemById(parsedItemId) is not { } item)
        {
            return new ProtectionPlaybackObservation(ProtectionPlaybackStatus.MissingItem);
        }

        var data = userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return new ProtectionPlaybackObservation(ProtectionPlaybackStatus.UnavailableUserData);
        }

        return data.Played
            ? new ProtectionPlaybackObservation(ProtectionPlaybackStatus.Played, data.LastPlayedDate?.ToUniversalTime(), user.Username)
            : new ProtectionPlaybackObservation(ProtectionPlaybackStatus.NotPlayed, data.LastPlayedDate?.ToUniversalTime(), user.Username);
    }
}
