using FF14Sound;

internal static class DialogueContextChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var context = new DialogueContext();
        var now = DateTimeOffset.UtcNow;
        void Line(string speaker, string text, string display = "Window", string language = "ja") =>
            context.Observe(speaker, text, language, display, now += TimeSpan.FromMilliseconds(250));
        Line("A", "First");
        Check(context.Previous.Length == 0, "First line has no earlier dialogue");
        var revision = context.Revision;
        for (var i = 0; i < 100; i++) Line("A", "First");
        Check(context.Revision == revision, "Repeated polls and slow reading do not duplicate or expire visible dialogue");
        Line("B", "Reply");
        Check(context.Previous.Single().Speaker == "A", "A speaker change retains the conversation");
        var snapshot = context.PreviousFor("B", "Reply", "ja");
        Check(context.PreviousFor("B", "Manual edit", "ja").Length == 0, "Manual text must not borrow the live game's context");
        Line("A", "Next");
        Check(snapshot.Length == 1 && snapshot[0].Text == "First", "An in-flight request keeps an immutable snapshot");
        for (var i = 0; i < 6; i++) Line("C", "Line " + i);
        Check(context.Previous.Length == 4 && context.Previous[0].Text == "Line 1" && context.Previous[^1].Text == "Line 4",
            "Context keeps only the four preceding utterances, oldest first");
        context.Observe(null, null, "ja", "None", now);
        Line("B", "Next after closing");
        Check(context.Previous[^1].Text == "Line 5", "A short window closure between lines preserves context");
        Line("B", "Subtitle", "Subtitle");
        Check(context.Previous.Length == 0, "Display surface changes end context");
        Line("B", "Subtitle reply", "Subtitle");
        Check(context.Previous.Length == 1, "Continuous subtitles retain context");
        now += TimeSpan.FromSeconds(6);
        Line("B", "After stalled reader", "Subtitle");
        Check(context.Previous.Length == 0, "A reader outage invalidates continuity");
        Line("B", "English", "Subtitle", "en");
        Check(context.Previous.Length == 0, "Source language changes reset context");
        foreach (var surface in new[] { "Notice", "Bubble", "Choice", "None" })
        {
            Line("B", "Non-dialogue", surface);
            Line("B", "After " + surface);
            Check(context.Previous.Length == 0, "Unsupported surfaces never bridge conversations");
        }
        context.Reset();
        Line("A", "Before transition");
        for (var i = 0; i < 6; i++)
        {
            // Simulate the real reader: each line closes, then a different speaker's box opens.
            for (var tick = 0; tick < 8; tick++)
                context.Observe(null, null, "ja", "None", now += TimeSpan.FromMilliseconds(250));
            Line(i % 2 == 0 ? "B" : "A", "Transition " + i);
        }
        Check(context.Previous.Length == 4 && context.Previous[0].Text == "Transition 1" &&
            context.Previous[^1].Text == "Transition 4", "Closing every dialogue box still retains the four previous lines");
        revision = context.Revision;
        context.Observe(null, null, "ja", "Window", now += TimeSpan.FromMilliseconds(250));
        Line("A", "Transition 5");
        Check(context.Revision == revision, "A duplicate after an empty-frame flicker must not become its own previous line");
        var gapStart = now += TimeSpan.FromMilliseconds(250);
        context.Observe(null, null, "ja", "None", gapStart);
        for (var tick = 1; tick < 480; tick++)
            context.Observe(null, null, "ja", "None", now = gapStart.AddMilliseconds(tick * 250));
        Check(context.Previous.Length == 4, "Context survives 119.75 seconds, including long speech and pauses");
        Check(DialogueContext.ClosedWindowGrace == TimeSpan.FromMinutes(2), "Dialogue timeout must allow two minutes");
        context.Observe(null, null, "ja", "None", now = gapStart + DialogueContext.ClosedWindowGrace);
        Check(context.Previous.Length == 0 && context.Revision != revision, "Two minutes without dialogue expires context and stale tone");
        Line("A", "New conversation");
        Check(context.Previous.Length == 0, "A later conversation starts with no old context");
        Line("B", "Reply");
        context.Observe(null, null, "ja", "None", now += TimeSpan.FromMilliseconds(250));
        Line("B", "Different display", "Subtitle");
        Check(context.Previous.Length == 0, "A gap must not hide an actual Window-to-Subtitle change");
        context.Reset();
        Line("A", new string('x', 1601));
        Line("B", "Short reply");
        Check(context.Previous.Length == 0, "Oversize context is dropped without clipping dialogue into misleading fragments");
        Line("C", "Next reply");
        Check(context.Previous.Length == 1 && context.Previous[0].Text == "Short reply", "Recent bounded context remains usable");
        context.Reset();
        Check(context.Previous.Length == 0 && context.PreviousFor("C", "Next reply", "ja").Length == 0,
            "Reconnect and explicit new-conversation reset discard all history");
        Console.WriteLine("PASS: dialogue context boundaries, speaker changes, duplicate polls, immutable snapshots, size limits and manual isolation.");
    }
}
