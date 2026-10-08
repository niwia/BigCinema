// A test runner with no xUnit in it on purpose. The point of this project is that it builds
// against pure-C# shims, so a test framework (and the NuGet restore it brings) would give up
// exactly that. A failing Check throws; the runner below catches it and prints the reason, so
// a suite reads as one line per test and exit code 1 when anything is red.
using System;
using System.Diagnostics;

namespace BigScreen.Tests;

internal static class Program
{
    private static int Main()
    {
        // Grouped roughly by the thing under test, in the order a reader wants to see them
        // fail: the queue rules, then the wire format that carries them.
        (string Name, Action Body)[] tests =
        {
            // --- host-side queue rules -------------------------------------------------
            ("QueueOps.AddAppendsTheItem", QueueOpsTests.AddAppendsTheItem),
            ("QueueOps.AddRejectsANonUrl", QueueOpsTests.AddRejectsANonUrl),
            ("QueueOps.AddRefusesPastTheLimit", QueueOpsTests.AddRefusesPastTheLimit),
            ("QueueOps.RemoveMovesTheCurrentIndex", QueueOpsTests.QueueRemoveMovesTheCurrentIndex),
            ("QueueOps.RemoveTheOnlyItemLeavesNothingCurrent", QueueOpsTests.RemoveTheOnlyItemLeavesNothingPlaying),
            ("QueueOps.RemoveOutOfRangeIsIgnored", QueueOpsTests.RemoveOutOfRangeIsIgnored),
            ("QueueOps.ClearEmptiesTheQueue", QueueOpsTests.ClearEmptiesTheQueue),
            ("QueueOps.PlayIndexLoadsTheEntryPaused", QueueOpsTests.PlayIndexLoadsTheEntryPaused),
            ("QueueOps.StepClampsAtBothEnds", QueueOpsTests.StepClampsAtBothEnds),
            ("QueueOps.StepMovesToTheNextItem", QueueOpsTests.StepMovesToTheNextItem),
            ("QueueOps.AutoAdvancePicksTheNextEntry", QueueOpsTests.AutoAdvancePicksTheNextEntry),

            // --- wire format -----------------------------------------------------------
            ("StateWire.StateRoundTrips", StateWireTests.StateRoundTrips),
            ("StateWire.StateRoundTripsAnEmptyQueue", StateWireTests.StateRoundTripsAnEmptyQueue),
            ("StateWire.StateRoundTripsOnlyUpToTheQueueLimit", StateWireTests.StateRoundTripsOnlyUpToTheQueueLimit),

            // --- clone -----------------------------------------------------------------
            ("SyncState.CloneGetsAFreshList", CloneTests.CloneGetsAFreshList),
        };

        Console.WriteLine($"BigScreen.Tests - the pure-logic half of the shared queue\n");

        var watch = Stopwatch.StartNew();
        int failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"  PASS  {test.Name}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"  FAIL  {test.Name}");
                Console.WriteLine($"        {e.Message.Replace("\n", "\n        ")}");
            }
        }
        watch.Stop();

        int passed = tests.Length - failed;
        Console.WriteLine();
        Console.WriteLine($"{passed}/{tests.Length} tests passed, {Check.Count} assertions, {watch.ElapsedMilliseconds} ms");
        if (failed > 0) Console.WriteLine($"{failed} FAILED");
        return failed == 0 ? 0 : 1;
    }
}
