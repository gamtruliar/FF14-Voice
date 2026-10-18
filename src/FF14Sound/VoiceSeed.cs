using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FF14Sound.Voices;

namespace FF14Sound;

public static class VoiceSeed
{
    public static uint Reroll(Settings settings, VoiceProfile voice)
    {
        if (string.IsNullOrWhiteSpace(voice.Speaker)) throw new InvalidOperationException("請先選擇角色。");
        uint seed;
        do { seed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)); }
        while (seed == voice.EffectiveSeed);
        settings.VoiceSeedOverrides[voice.EffectiveVoiceIdentity] = seed;
        return seed;
    }
    public static VoiceProfile ApplyOverride(Settings settings, VoiceProfile voice)
    {
        if (settings.VoiceSeedOverrides.TryGetValue(voice.EffectiveVoiceIdentity, out var seed)) voice.Seed = seed;
        return voice;
    }
    // Stable across processes/machines; never use string.GetHashCode for a persisted voice.
    public static uint For(string identity) => BinaryPrimitives.ReadUInt32LittleEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes("ff14sound-voice-seed-v1:" + identity)));

    public static uint ForSpeaker(string speaker) => string.IsNullOrWhiteSpace(speaker)
        ? 42 : For("name:" + VoiceResolver.Normalize(speaker));

    public static uint ForNpc(string speaker, string? identity) => string.IsNullOrWhiteSpace(identity) || identity.StartsWith("live:", StringComparison.Ordinal)
        ? ForSpeaker(speaker) : For("npc:" + identity);
}
