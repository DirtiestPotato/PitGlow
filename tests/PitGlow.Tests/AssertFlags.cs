namespace PitGlow.Tests;

internal static class AssertFlags
{
    public static void Equal(IReadOnlySet<Flag> actual, params Flag[] expected) =>
        Assert.True(actual.SetEquals(expected),
                    $"expected {{{string.Join(", ", expected)}}}, got {{{string.Join(", ", actual)}}}");
}
