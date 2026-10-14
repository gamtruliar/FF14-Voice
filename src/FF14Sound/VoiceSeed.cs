using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FF14Sound.Voices;

namespace FF14Sound;

public static class VoiceSeed
{
    // Stable across processes/machines; never use string.GetHashCode for a persisted voice.
    public static uint For(string identity) => BinaryPrimitives.ReadUInt32LittleEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes("ff14sound-voice-seed-v1:" + identity)));

    public static uint ForSpeaker(string speaker) => string.IsNullOrWhiteSpace(speaker)
        ? 42 : For("name:" + VoiceResolver.Normalize(speaker));

    public static uint ForNpc(string speaker, string? identity) => string.IsNullOrWhiteSpace(identity) || identity.StartsWith("live:", StringComparison.Ordinal)
        ? ForSpeaker(speaker) : For("npc:" + identity);
}
