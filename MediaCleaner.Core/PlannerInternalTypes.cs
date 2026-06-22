using System.Collections.Generic;

namespace MediaCleaner.Core;

internal sealed record CandidateItem(MediaItem Item, IReadOnlyList<PlaybackState> Playback);

internal sealed record RuleMatch(CleanupRule Rule, MediaItem Item, ExpiredKind Kind, IReadOnlyList<PlaybackState> Playback);
