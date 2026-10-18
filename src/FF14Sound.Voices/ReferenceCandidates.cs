using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;

namespace FF14Sound.Voices;

public sealed record ReferenceCandidate(string File, string Token, string Language, double Seconds, string Text, string GamePath, string Sheet);

public static class ReferenceCandidates
{
    public static async Task ExportAll(string sqpack, string manifest, string catalogPath, string output, CancellationToken token)
    {
        sqpack = Path.GetFullPath(sqpack); output = Path.GetFullPath(output);
        if (output.Equals(sqpack, StringComparison.OrdinalIgnoreCase) || output.StartsWith(sqpack + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Candidate directory must be outside the game archive.");
        Directory.CreateDirectory(output);
        var catalog = VoiceCatalog.Load(catalogPath);
        var wanted = catalog.Voices.SelectMany(v => v.References.Select(r => (v.Token, r.Language))).ToHashSet();
        var clips = JsonSerializer.Deserialize<GameClip[]>(await File.ReadAllTextAsync(manifest, token)) ?? throw new InvalidDataException("Empty manifest");
        var selected = clips.Where(c => wanted.Contains((c.SpeakerToken, c.Language))).DistinctBy(c => (c.SpeakerToken, c.Language, c.GamePath)).OrderBy(c => c.GamePath).ToArray();
        using var game = new GameData(sqpack);
        var candidates = new List<ReferenceCandidate>();
        var errors = new List<string>();
        var processed = 0;
        foreach (var clip in selected)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var audio = game.GetFile<ScdFile>(clip.GamePath)?.GetAudio(0);
                if (audio?.AudioDataHeader is not AdpcmWaveFormat fmt || fmt.BlockAlign == 0 || fmt.SamplesPerSec == 0)
                    throw new InvalidDataException("Unsupported audio encoding");
                var seconds = (double)(audio.AudioData.Length / fmt.BlockAlign) * fmt.SamplesPerBlock / fmt.SamplesPerSec;
                if (seconds is >= 3 and <= 12)
                {
                    var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clip.GamePath))).ToLowerInvariant() + ".wav";
                    var destination = Path.Combine(output, name);
                    if (!File.Exists(destination))
                    {
                        CatalogBuilder.WriteAdpcm(audio, destination + ".tmp");
                        File.Move(destination + ".tmp", destination, true);
                    }
                    candidates.Add(new(name, clip.SpeakerToken, clip.Language, seconds, clip.Text, clip.GamePath, clip.Sheet));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(clip.GamePath + ": " + ex.Message); }
            if (++processed % 1000 == 0) Console.WriteLine($"Extracted {processed}/{selected.Length}; {candidates.Count} eligible; {errors.Count} errors");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "candidates.json"), JsonSerializer.Serialize(candidates, VoiceCatalog.JsonOptions), token);
        await File.WriteAllTextAsync(Path.Combine(output, "extraction-errors.json"), JsonSerializer.Serialize(errors, VoiceCatalog.JsonOptions), token);
        Console.WriteLine($"COMPLETE: {candidates.Count} complete 3–12 s candidates / {selected.Length} source clips; {errors.Count} errors.");
    }

    // Export complete utterances only. This pilot never changes the active catalog/reference.
    public static async Task Export(string sqpack, string manifest, string output, string speaker, string language, CancellationToken token)
    {
        sqpack = Path.GetFullPath(sqpack); output = Path.GetFullPath(output);
        if (output.Equals(sqpack, StringComparison.OrdinalIgnoreCase) || output.StartsWith(sqpack + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Candidate directory must be outside the game archive.");
        var clips = JsonSerializer.Deserialize<GameClip[]>(await File.ReadAllTextAsync(manifest, token)) ?? throw new InvalidDataException("Empty manifest");
        var selected = clips.Where(c => c.SpeakerToken == speaker && c.Language == language).DistinctBy(c => c.GamePath).OrderBy(c => c.GamePath).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("No matching voice clips");
        Directory.CreateDirectory(output);
        using var game = new GameData(sqpack);
        var candidates = new List<ReferenceCandidate>();
        var errors = new List<string>();
        foreach (var clip in selected)
        {
            token.ThrowIfCancellationRequested();
            string? temporary = null;
            string? staging = null;
            try
            {
                var audio = game.GetFile<ScdFile>(clip.GamePath)?.GetAudio(0);
                if (audio?.AudioDataHeader is not AdpcmWaveFormat fmt || fmt.BlockAlign == 0 || fmt.SamplesPerSec == 0) continue;
                var seconds = (double)(audio.AudioData.Length / fmt.BlockAlign) * fmt.SamplesPerBlock / fmt.SamplesPerSec;
                if (seconds is < 3 or > 12) continue;
                var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clip.GamePath))).ToLowerInvariant() + ".wav";
                var destination = Path.Combine(output, name);
                temporary = destination + ".source.wav"; staging = destination + ".new.wav";
                CatalogBuilder.WriteAdpcm(audio, temporary);
                var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-v", "error", "-nostdin", "-y", "-i", temporary, "-ac", "1", "-ar", "24000", "-c:a", "pcm_s16le", staging }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Cannot start ffmpeg");
                var stderr = process.StandardError.ReadToEndAsync(token);
                try { await process.WaitForExitAsync(token); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                var message = await stderr;
                if (process.ExitCode != 0) throw new IOException(message);
                File.Move(staging, destination, true);
                candidates.Add(new(name, speaker, language, seconds, clip.Text, clip.GamePath, clip.Sheet));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(clip.GamePath + ": " + ex.Message); }
            finally
            {
                if (temporary is not null) File.Delete(temporary);
                if (staging is not null) File.Delete(staging);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "candidates.json"), JsonSerializer.Serialize(candidates, VoiceCatalog.JsonOptions), token);
        await File.WriteAllTextAsync(Path.Combine(output, "extraction-errors.json"), JsonSerializer.Serialize(errors, VoiceCatalog.JsonOptions), token);
        Console.WriteLine($"{speaker}/{language}: {candidates.Count} complete 3–12 s candidates out of {selected.Length} clips; {errors.Count} errors.");
    }
}
