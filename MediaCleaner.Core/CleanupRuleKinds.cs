using System;

namespace MediaCleaner.Core;

internal static class CleanupRuleKinds
{
    public static ExpiredKind ToExpiredKind(CleanupRuleTriggerKind kind) => kind switch
    {
        CleanupRuleTriggerKind.Played => ExpiredKind.Played,
        CleanupRuleTriggerKind.NotPlayed => ExpiredKind.NotPlayed,
        CleanupRuleTriggerKind.AddedAge => ExpiredKind.AddedAge,
        _ => throw new NotSupportedException($"Unsupported rule trigger: {kind}"),
    };

    public static int Priority(ExpiredKind kind) => kind switch
    {
        ExpiredKind.Played => 0,
        ExpiredKind.NotPlayed => 1,
        ExpiredKind.AddedAge => 2,
        _ => throw new NotSupportedException($"Unsupported expired kind: {kind}"),
    };
}
