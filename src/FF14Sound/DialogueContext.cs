using System.Text.Json.Serialization;

namespace FF14Sound;

public sealed record DialogueContextLine(
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("language")] string Language);

/// <summary>Conservative continuity, not a claim to know the game's scene/event ID.</summary>
public sealed class DialogueContext
{
    public const int MaxPreviousLines = 4;
    public const int MaxCharacters = 1600;
    public static readonly TimeSpan ClosedWindowGrace = TimeSpan.FromMinutes(2);
    private readonly List<DialogueContextLine> lines = [];
    private string surface = "";
    private DateTimeOffset? lastPoll;
    private DateTimeOffset? closedSince;
    public long Revision { get; private set; }
    public DialogueContextLine[] Previous { get; private set; } = [];
    public DialogueContextLine[] PreviousFor(string speaker, string text, string language) =>
        lines.Count > 0 && lines[^1] == new DialogueContextLine(speaker.Trim(), text.Trim(), language)
            ? Previous.ToArray() : [];

    public void Reset()
    {
        lines.Clear(); Previous = []; surface = ""; lastPoll = null; closedSince = null; Revision++;
    }

    // Called on EVERY poll, including duplicate lines and an empty/hidden dialogue box.
    public bool Observe(string? speaker, string? text, string language, string display, DateTimeOffset now)
    {
        var supported = display is "Window" or "Subtitle";
        if ((lastPoll is { } last && (now - last > TimeSpan.FromSeconds(5) || now < last)) ||
            (lines.Count > 0 && lines[^1].Language != language)) Reset();
        // A line transition normally closes the box. An observed short gap is not a scene boundary.
        // Keep the last real surface, so Window -> None -> Window remains the same candidate conversation.
        if (!supported && display != "None")
        {
            if (lines.Count > 0) Reset();
            return false;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            if (lines.Count == 0) return false;
            closedSince ??= now;
            lastPoll = now;
            if (now - closedSince.Value >= ClosedWindowGrace) Reset();
            return false;
        }
        if (!supported)
        {
            if (lines.Count > 0) Reset();
            return false;
        }
        if (surface != display || (closedSince is { } closed && now - closed >= ClosedWindowGrace)) Reset();
        surface = display; lastPoll = now; closedSince = null;
        var current = new DialogueContextLine((speaker ?? "").Trim(), text.Trim(), language);
        if (lines.Count > 0 && lines[^1] == current) return false;
        var previous = new List<DialogueContextLine>();
        var size = 0;
        foreach (var line in lines.AsEnumerable().Reverse().Take(MaxPreviousLines))
        {
            size += line.Speaker.Length + line.Text.Length + line.Language.Length;
            if (size > MaxCharacters) break;
            previous.Insert(0, line);
        }
        Previous = previous.ToArray();
        lines.Add(current);
        while (lines.Count > MaxPreviousLines) lines.RemoveAt(0);
        Revision++;
        return true;
    }
}
