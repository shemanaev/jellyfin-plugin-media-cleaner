#if JELLYFIN_USER_IN_DATA_ENTITIES
global using JellyfinUser = Jellyfin.Data.Entities.User;
global using JellyfinSortOrder = Jellyfin.Data.Enums.SortOrder;
global using JellyfinActivityLog = Jellyfin.Data.Entities.ActivityLog;
#else
global using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
global using JellyfinSortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;
global using JellyfinActivityLog = Jellyfin.Database.Implementations.Entities.ActivityLog;
#endif
