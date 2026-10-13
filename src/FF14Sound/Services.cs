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

public sealed class Translator
{
    private readonly WebTranslator translator = new(NullLogger.Instance);
    public Translator() => translator.LoadLanguages();
    public async Task<string> Translate(string text, string from, string to, CancellationToken token)
    {
        if (from == to) return text;
        var engine = translator.TranslationEngines.First(e => e.EngineName == TranslationEngineName.GoogleTranslate);
        TranslatorLanguage Find(string code) => engine.SupportedLanguages.FirstOrDefault(l => l.LanguageCode == code)
            ?? throw new InvalidOperationException($"Translation language unavailable: {code}");
        var result = await translator.TranslateAsync(text, engine, Find(from), Find(to), token);
        if (!result.IsSuccess) throw new InvalidOperationException(result.FailureReason);
        return result.Text;
    }
}

public sealed class SpeechClient : IDisposable
{
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
    public async Task CancelPending(string server, int version)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var result = await client.PostAsJsonAsync(Endpoint(server, "/api/cancel"), new { session_id = SessionId, revision = version }, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException or UriFormatException or InvalidOperationException) { }
    }
    public async Task<byte[]> Generate(string server, string text, VoiceProfile voice, CancellationToken token, int version = 0, string? cacheFolder = null)
    {
        if (!File.Exists(voice.ReferenceAudio)) throw new InvalidOperationException("角色聲音樣本不存在，請先選擇音檔。");
        var endpoint = Endpoint(server, "/api/tts");
        string? cache = null;
        if (cacheFolder is not null)
        {
            var referenceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(voice.ReferenceAudio, token)));
            var key = System.Text.Json.JsonSerializer.Serialize(new { schema = "voxcpm2-v1", server, text, referenceHash, voice.Language, voice.Transcript, voice.Control });
            cache = Path.Combine(cacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".wav");
            if (File.Exists(cache)) return await File.ReadAllBytesAsync(cache, token);
        }
        using var response = await client.PostAsJsonAsync(endpoint, new
        {
            text, language = voice.Language, reference_audio = voice.ReferenceAudio,
            transcript = voice.Transcript, control = voice.Control, session_id = SessionId, revision = version
        }, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) throw new OperationCanceledException("Dialogue superseded.", token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(token));
        var wav = await response.Content.ReadAsByteArrayAsync(token);
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") throw new InvalidDataException("VoxCPM 未回傳有效 WAV。");
        token.ThrowIfCancellationRequested();
        if (cache is not null)
        {
            Directory.CreateDirectory(cacheFolder!);
            var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, wav, token); File.Move(temp, cache, true); }
            finally { File.Delete(temp); }
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
