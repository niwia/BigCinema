// Just enough of an assertion library to be readable in a failure message. There is no xUnit
// here on purpose (no package restore, no test adapter): a failed Check throws
// Check.Failed, and the runner in Program.cs turns that into one FAIL line.
using System;
using System.Collections.Generic;

namespace BigScreen.Tests;

internal static class Check
{
    public static int Count { get; private set; }

    public static void True(bool condition, string what)
    {
        Count++;
        if (!condition) throw new Failed(what ?? "expected true");
    }

    public static void False(bool condition, string what) => True(!condition, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        string message = $"{what}: expected '{expected}', got '{actual}'";
        True(EqualityComparer<T>.Default.Equals(expected, actual), message);
    }

    /// <summary>Different instances, used to prove a copy really did copy the list.</summary>
    public static void NotSame(object expected, object actual, string what) =>
        True(!ReferenceEquals(expected, actual), $"{what}: expected a different instance");

    public static void Null(object value, string what) =>
        True(value == null, $"{what}: expected null, got '{value}'");

    /// <summary>
    /// Compares a whole state in ONE assertion, so a round-trip failure prints a diff of every
    /// field that disagrees instead of stopping at the first one.
    /// </summary>
    public static void SameState(SyncState expected, SyncState actual, string what)
    {
        var problems = new List<string>();
        Field(expected.Revision, actual.Revision, "Revision", problems);
        Field(expected.VideoUrl, actual.VideoUrl, "VideoUrl", problems);
        Field(expected.Title, actual.Title, "Title", problems);
        Field(expected.Playing, actual.Playing, "Playing", problems);
        Field(expected.AnchorNetTime, actual.AnchorNetTime, "AnchorNetTime", problems);
        Field(expected.AnchorVideoTime, actual.AnchorVideoTime, "AnchorVideoTime", problems);
        Field(expected.QueueIndex, actual.QueueIndex, "QueueIndex", problems);
        Field(expected.AutoAdvance, actual.AutoAdvance, "AutoAdvance", problems);

        int expectedCount = expected.Queue == null ? 0 : expected.Queue.Count;
        int actualCount = actual.Queue == null ? 0 : actual.Queue.Count;
        if (expectedCount != actualCount) problems.Add($"Queue.Count: expected {expectedCount}, got {actualCount}");
        else
        {
            for (int i = 0; i < expectedCount; i++)
            {
                Field(expected.Queue[i].Url, actual.Queue[i].Url, $"Queue[{i}].Url", problems);
                Field(expected.Queue[i].Title, actual.Queue[i].Title, $"Queue[{i}].Title", problems);
            }
        }

        True(problems.Count == 0,
            problems.Count == 0 ? what : $"{what}:\n          " + string.Join("\n          ", problems));
    }

    private static void Field<T>(T expected, T actual, string name, List<string> problems)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            problems.Add($"{name}: expected '{expected}', got '{actual}'");
    }

    internal sealed class Failed : Exception
    {
        public Failed(string message) : base(message) { }
    }
}
