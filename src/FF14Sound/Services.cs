using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FFXIVTataruHelper.Services.GameMemory;
using FFXIVTataruHelper.Services.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sharlayan.Models;
using Translation;
using Translation.Models;

namespace FF14Sound;

public sealed class Translator : IDisposable
{
    private readonly OllamaTranslation ollama = new();
    private readonly GeminiTranslation gemini = new();
    private readonly WebTranslator translator = new(NullLogger.Instance);
    public Translator() => translator.LoadLanguages();
    public Task<string[]> OllamaModels(string address, CancellationToken token, CloudflareAccess? access = null) => ollama.Models(address, token, access);
    public Task<string[]> GeminiModels(string server, string keyFile, CancellationToken token) => gemini.Models(server, keyFile, token);
    public void Dispose() { ollama.Dispose(); gemini.Dispose(); }
    public async Task<string> Translate(string text, string from, string to, CancellationToken token, TranslationOptions? options = null, string speaker = "")
    {
        if (from == to) return text;
        if (options?.Engine == "ollama") return await ollama.Translate(text, from, to, speaker, options, token);
        if (options?.Engine == "gemini") return await gemini.Translate(text, from, to, speaker, options, token);
        if (options is not null && options.Engine != "google") throw new InvalidOperationException("Unknown translation engine: " + options.Engine);
        var engine = translator.TranslationEngines.First(e => e.EngineName == TranslationEngineName.GoogleTranslate);
        TranslatorLanguage Find(string code) => engine.SupportedLanguages.FirstOrDefault(l => l.LanguageCode == code)
            ?? throw new InvalidOperationException($"Translation language unavailable: {code}");
        var result = await translator.TranslateAsync(text, engine, Find(from), Find(to), token);
        if (!result.IsSuccess) throw new InvalidOperationException(result.FailureReason);
        return result.Text;
    }
}

public sealed partial class SpeechClient : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, long Length, string Hash)> referenceHashes = new();
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public SpeechClient() { }
    public SpeechClient(HttpMessageHandler handler) { client.Dispose(); client = new(handler) { Timeout = TimeSpan.FromMinutes(10) }; }
    private static Uri Endpoint(string server, string path)
    {
        var uri = new Uri(server);
        if (!uri.IsLoopback || uri.Scheme != "http") throw new InvalidOperationException("VoxCPM server 必須係本機 http 地址。");
        return new Uri(uri, path);
    }
    public async Task WarmUp(string server, CancellationToken token)
    {
        using var result = await client.PostAsync(Endpoint(server, "/api/warmup"), null, token);
        result.EnsureSuccessStatusCode();
    }
    public async Task EnsureBackgroundSupport(string server, CancellationToken token)
    {
        var health = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(Endpoint(server, "/api/health"), token);
        if (!health.TryGetProperty("background_priority", out var enabled) || !enabled.GetBoolean())
            throw new InvalidOperationException("請先重啟新版 VoxCPM backend，先可以背景預配及讓即時對白優先。");
    }
    public async Task CancelPending(string server, int version)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var result = await client.PostAsJsonAsync(Endpoint(server, "/api/cancel"), new { session_id = SessionId, revision = version }, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException or UriFormatException or InvalidOperationException) { }
    }
    public static string CacheFolder(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SpeechCacheFolder)) return Path.GetFullPath(settings.SpeechCacheFolder);
        var catalog = string.IsNullOrWhiteSpace(settings.CatalogPath) ? GameVoiceLibrary.FindCatalog() : settings.CatalogPath;
        return catalog.Length > 0 ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(catalog)))!, "speech-cache") : Path.Combine(Settings.Folder, "speech-cache");
    }
    public async Task<string> CachePath(string server, string text, VoiceProfile voice, string folder, CancellationToken token)
    {
        string? referenceHash = null;
        if (!voice.IsFallback)
        {
            var info = new FileInfo(voice.ReferenceAudio);
            if (!info.Exists) throw new FileNotFoundException("角色聲音樣本不存在", voice.ReferenceAudio);
            if (!referenceHashes.TryGetValue(info.FullName, out var cached) || cached.Stamp != info.LastWriteTimeUtc.Ticks || cached.Length != info.Length)
            {
                cached = (info.LastWriteTimeUtc.Ticks, info.Length, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(info.FullName, token))));
                referenceHashes[info.FullName] = cached;
            }
            referenceHash = cached.Hash;
        }
        // Reader and EXD can use different line endings. Preserve all actual words and punctuation.
        text = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        // A learned voice is an immutable first take for this identity/seed/direction. Keeping
        // its cache identity stable lets the first sentence and prepared jobs remain reusable.
        var key = System.Text.Json.JsonSerializer.Serialize(new { schema = "voxcpm2-v4-first-voice", server, text, identity = voice.EffectiveVoiceIdentity,
            seed = voice.EffectiveSeed, mode = voice.IsFallback ? "learned-first" : "clone", referenceHash,
            voice.Language, transcript = voice.IsFallback ? "" : voice.Transcript, voice.Control });
        return Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".wav");
    }
    public static bool IsCached(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[12];
            return stream.Length > 44 && stream.Read(header) == 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8);
        }
        catch (IOException) { return false; }
    }
    public async Task<byte[]> Generate(string server, string text, VoiceProfile voice, CancellationToken token, int version = 0, string? cacheFolder = null, bool background = false)
    {
        using var cacheLease = cacheFolder is null ? null : SpeechCache.Lease(cacheFolder);
        using var voiceLock = cacheFolder is null ? null : await SpeechCache.LockVoice(cacheFolder, voice, token);
        // Ignore stale generic default refs; only this NPC's own completed first take can be learned.
        var reference = voice.IsFallback ? cacheFolder is null ? null : SpeechCache.LearnedReference(cacheFolder, voice) : voice.ReferenceAudio;
        var transcript = voice.IsFallback ? "" : voice.Transcript;
        if (!voice.IsFallback && !File.Exists(reference)) throw new InvalidOperationException("角色聲音樣本不存在，請先選擇音檔。");
        var endpoint = Endpoint(server, "/api/tts");
        string? cache = null;
        if (cacheFolder is not null)
        {
            cache = await CachePath(server, text, voice, cacheFolder, token);
            if (IsCached(cache))
            {
                SpeechCache.Complete(cacheFolder, cache, voice, token);
                return await File.ReadAllBytesAsync(cache, token);
            }
        }
        using var response = await client.PostAsJsonAsync(endpoint, new
        {
            text, language = voice.Language, reference_audio = reference,
            transcript, control = voice.Control, seed = voice.EffectiveSeed, session_id = SessionId, revision = version, priority = background ? "background" : "interactive"
        }, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) throw new OperationCanceledException("Dialogue superseded.", token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(token));
        var wav = await response.Content.ReadAsByteArrayAsync(token);
        if (wav.Length <= 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") throw new InvalidDataException("VoxCPM 未回傳有效 WAV。");
        token.ThrowIfCancellationRequested();
        if (cache is not null)
        {
            Directory.CreateDirectory(cacheFolder!);
            var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, wav, token); File.Move(temp, cache, true); }
            finally { File.Delete(temp); }
            SpeechCache.Complete(cacheFolder!, cache, voice, token);
        }
        return wav;
    }
    public void Dispose() => client.Dispose();
}

public sealed class GameReader : IDisposable
{
    private readonly SharlayanGameMemoryGateway gateway = new(new HeuristicDirectDialogReader(), new ReaderLogger());
    private Process? process;
    public void Attach(string language)
    {
        process = Process.GetProcessesByName("ffxiv_dx11").FirstOrDefault()
            ?? throw new InvalidOperationException("未搵到 ffxiv_dx11，請先啟動遊戲。");
        gateway.SetProcess(new ProcessModel { Process = process }, language, "", true, false);
    }
    public (string Speaker, string Text)? Read()
    {
        if (process?.HasExited != false) throw new InvalidOperationException("FF14 已關閉，請重新連接。");
        gateway.GetDirectDialog();
        var line = gateway.CurrentDialogueLine;
        var speaker = gateway.CurrentDialogueSpeaker;
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (!string.IsNullOrEmpty(speaker) && line.StartsWith(speaker + ":", StringComparison.Ordinal))
            line = line[(speaker.Length + 1)..].Trim();
        return (speaker, line);
    }
    public void Dispose() { gateway.Dispose(); process?.Dispose(); }
    private sealed class ReaderLogger : IAppLogger
    {
        public void WriteLog(string input, string memberName = "", int sourceLineNumber = 0) => Trace.WriteLine(input);
        public void WriteLog(object input, string memberName = "", int sourceLineNumber = 0) => Trace.WriteLine(input);
        public void WriteConsoleLog(string input) => Trace.WriteLine(input);
        public void WriteChatLog(string input) => Trace.WriteLine(input);
    }
}
