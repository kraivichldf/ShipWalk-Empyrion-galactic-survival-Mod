using System;
using ShipWalk;

internal static class DiagnosticWindowTests
{
    internal static void CaptureBounds()
    {
        var window = new DiagnosticWindow();
        Check(!window.Active(0), "Must not capture until a departure.");
        window.Open(551.3f);
        // The live 0.1.12 case loses support at +0.525 s. Capture must include
        // both that transition and subsequent callbacks, independently of it.
        for (int step = 0; step <= 40; step++)
        {
            float now = 551.3f + step * 0.025f;
            for (int callback = 0; callback < 10; callback++)
                Check(window.Take(now), "Normal first-second callback capture ended early.");
        }
        Check(!window.Take(552.325f), "Capture outlived its simulation-time bound.");
        window.Open(600f);
        for (int i = 0; i < DiagnosticWindow.MaxCoreEvents; i++)
            Check(window.Take(600f), "Event bound was applied early.");
        Check(!window.Take(600f) && window.Active(600.025f), "Core budget must be bounded independently of time and contacts.");
        Check(window.TakeContactPair(600.025f), "Core budget exhaustion must not consume contact reserve.");
    }

    internal static void RecordedContactStorm()
    {
        var window = new DiagnosticWindow();
        window.Open(212.675f);
        // 0.1.14 exhausted all 1024 events at +0.25 s. Keep physics and
        // limiter evidence at support loss (+0.325) and speed loss (+0.450).
        for (int step = 0; step <= 40; step++)
        {
            float now = 212.675f + step * .025f;
            for (int callback = 0; callback < 200; callback++) window.TakeContactPair(now);
            for (int callback = 0; callback < 10; callback++)
                Check(window.Take(now), "Contact storm consumed reserved core events before the full second.");
        }
        Check(window.ContactPairs == 82 && window.CoreEvents == 410 && window.DroppedContacts == 8118,
            "Before/after pairs must be reserved together and omitted pairs counted.");
        Check(!window.TakeContactPair(213.7f) && !window.Take(213.7f), "Both channels stop at the time bound.");
        window.Open(0f);
        for (int step = 0; step < 1000; step++) window.TakeContactPair(step * .001f);
        Check(window.ContactPairs == DiagnosticWindow.MaxContactPairs && window.Take(.999f), "Total contact cap leaves core channel usable.");
    }

    internal static void TraceReservation()
    {
        var budget = new TraceBudget();
        Check(budget.Take(TraceBudget.RoutineBytes, false), "Routine allocation rejected early.");
        Check(!budget.Take(1, false), "Routine spam consumed the exit reserve.");
        Check(budget.Take(TraceBudget.MaxBytes - TraceBudget.RoutineBytes, true), "Exit reserve missing after routine cap.");
        Check(!budget.Take(1, true) && budget.Used == TraceBudget.MaxBytes, "Session size is not bounded.");
        Check(!budget.Take(-1, true), "Invalid length accepted.");
        var cadence = new TraceBudget();
        Check(cadence.Sample("fixed", 1f) && !cadence.Sample("fixed", 1.025f), "Routine capture must not depend on mutable passenger sampling flags.");
        Check(cadence.Sample("late-limiter", 1.025f) && cadence.Sample("fixed", 1.25f), "Independent event kinds and later samples retained.");
        Check(cadence.Sample("fixed", 0f) && !cadence.Sample("fixed", float.NaN), "Clock reset or invalid time handling failed.");
    }

    internal static void Lifecycle()
    {
        var window = new DiagnosticWindow();
        window.Open(1f);
        Check(window.Take(1f), "Capture did not start.");
        window.Close();
        Check(!window.Take(1.025f), "Cancelled capture resumed.");
        window.Open(float.NaN);
        Check(!window.Take(1f), "Invalid departure time accepted.");
        window.Open(2f);
        Check(!window.Take(float.PositiveInfinity), "Invalid sample time accepted.");
        window.Open(2f);
        Check(!window.Take(1.9f), "Reversed clock retained old capture.");
        window.Open(3f);
        Check(window.Events == 0 && window.Take(3f) && window.Events == 1, "Next departure did not restart capture.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
}
