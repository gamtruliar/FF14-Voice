using System.Text.Json;
using System.IO;
using FF14Sound;

internal static class VoiceSeedChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(string folder)
    {
        var library = new GameVoiceLibrary();
        var settings = new Settings();
        var first = library.Resolve("NPC A", "ja", settings);
        var next = new GameVoiceLibrary().Resolve("NPC A", "yue", new Settings());
        Check(first.Seed.HasValue && first.EffectiveSeed == next.EffectiveSeed, "Same NPC keeps seed across library instances/languages");
        Check(first.EffectiveSeed != library.Resolve("NPC B", "ja", settings).EffectiveSeed, "Different NPC gets different seed");
        Check(first.EffectiveSeed == library.Resolve("NPC A", "ja", settings, npcIdentity: "live:npca").EffectiveSeed, "First encounter does not change seed");
        var ja = library.Resolve("日本語名", "ja", settings, npcIdentity: "npc:123");
        var en = library.Resolve("English name", "en", settings, npcIdentity: "npc:123");
        Check(ja.EffectiveSeed == en.EffectiveSeed, "Canonical NPC ID keeps seed across localized names");
        var original = SpeechSelection.Choose("original", "a", "b", "ja", "yue").Apply(ja);
        Check(original.Seed == ja.Seed, "Original dubbing preserves seed");
        var restored = JsonSerializer.Deserialize<VoiceProfile>(JsonSerializer.Serialize(ja))!;
        Check(restored.Seed == ja.Seed, "Serialized profile and batch snapshots persist seed");
        var custom = new VoiceProfile { Speaker = "NPC A", Seed = uint.MaxValue };
        settings.Voices.Add(custom);
        Check(library.Resolve("NPC A", "ja", settings).EffectiveSeed == uint.MaxValue, "Explicit seed is preserved including full uint range");
        custom.Seed = 0;
        Check(library.Resolve("NPC A", "ja", settings).EffectiveSeed == 0, "Zero is a valid fixed seed");
        using var speech = new SpeechClient();
        var a = await speech.CachePath("http://localhost:8808", "hello", first, folder, CancellationToken.None);
        first.Seed = first.EffectiveSeed ^ 1;
        var b = await speech.CachePath("http://localhost:8808", "hello", first, folder, CancellationToken.None);
        Check(a != b, "Changed seed cannot replay audio cached with previous seed");
        var retrySettings = new Settings();
        var oldVoice = library.Resolve("Retry NPC", "ja", retrySettings, npcIdentity: "123");
        var oldCache = await speech.CachePath("http://localhost:8808", "same line", oldVoice, folder, CancellationToken.None);
        File.WriteAllBytes(oldCache, SpeechCacheChecks.Wave());
        SpeechCache.Complete(folder, oldCache, oldVoice, CancellationToken.None);
        var oldReference = SpeechCache.LearnedReference(folder, oldVoice);
        var newSeed = VoiceSeed.Reroll(retrySettings, oldVoice);
        var settingsFile = Path.Combine(folder, "retry-seed-settings.json");
        retrySettings.Save(settingsFile);
        var retryVoice = new GameVoiceLibrary().Resolve("Retry NPC localized", "yue", Settings.Load(settingsFile), npcIdentity: "123");
        Check(newSeed != oldVoice.EffectiveSeed && retryVoice.EffectiveSeed == newSeed, "Reroll persists across restart and localized names");
        Check(SpeechCache.LearnedReference(folder, retryVoice) is null && File.Exists(oldReference), "New seed starts without previous learned reference and preserves its file");
        var newCache = await speech.CachePath("http://localhost:8808", "same line", retryVoice, folder, CancellationToken.None);
        Check(newCache != oldCache, "Rerolled voice must not replay previous sentence cache");
        File.WriteAllBytes(newCache, SpeechCacheChecks.Wave());
        SpeechCache.Complete(folder, newCache, retryVoice, CancellationToken.None);
        Check(SpeechCache.LearnedReference(folder, retryVoice) is not null && SpeechCache.LearnedReference(folder, retryVoice) != oldReference && File.Exists(oldCache), "Retry learns a separate reference without removing previous audio");
        Check(library.Resolve("Another NPC", "ja", retrySettings).EffectiveSeed == library.Resolve("Another NPC", "ja", new Settings()).EffectiveSeed, "Reroll does not change other NPCs");
        Check(library.Resolve("Retry NPC", "ja", retrySettings, npcIdentity: "123").EffectiveSeed == newSeed, "Next sentence keeps rerolled seed");
        Console.WriteLine("PASS: per-NPC stable seeds, localized identity, profile persistence, original dubbing and cache separation.");
    }
}
