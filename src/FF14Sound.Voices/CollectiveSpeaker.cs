namespace FF14Sound.Voices;

/// <summary>Exact dialogue labels only; never infer a crowd from plural NPC IDs or a name substring.</summary>
public static class CollectiveSpeaker
{
    private static readonly HashSet<string> Names = new(new[]
    {
        "一同", "全員", "皆", "みんな", "皆さん", "眾人", "众人", "大家", "全體", "全体",
        "All", "Everyone", "Everybody", "All together", "The crowd", "Crowd",
        "Tous", "Tout le monde", "Alle", "Alle zusammen"
    }.Select(VoiceResolver.Normalize));

    public const string Description = "多人齊聲回應，幾把不同嘅自然中低音及中音同時講同一句；節奏一致，咬字有力，尾音乾淨。";
    public static bool IsCollective(string name) => Names.Contains(VoiceResolver.Normalize(name));
}
