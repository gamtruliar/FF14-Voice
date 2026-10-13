using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FF14Sound;
using FF14Sound.Voices;

internal static class IntegrationChecks
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ff14sound-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var reference = Path.Combine(folder, "ref.wav");
        await File.WriteAllBytesAsync(reference, "test reference"u8.ToArray());
        var first = new GameVoice("YSHTOLA", ["Y'shtola", "ヤ・シュトラ", "Shared"], [new("ja", "ref.wav", 8, "", "path1", "sheet")]);
        var second = new GameVoice("TATARU", ["Tataru", "Shared"], [new("ja", "ref.wav", 8, "", "path2", "sheet")]);
        var catalog = new VoiceCatalog(1, "game", DateTimeOffset.Now, [first, second], []);
        var resolver = new VoiceResolver(catalog, folder);
        Check(resolver.Resolve("Y’shtola", "ja")?.Voice.Token == "YSHTOLA", "Unicode apostrophe alias");
        Check(resolver.Resolve("ヤ・シュトラ", "ja")?.Voice.Token == "YSHTOLA", "Japanese NPC alias");
        Check(resolver.Resolve("Shared", "ja") is null, "Ambiguous actor must not be guessed");
        Check(resolver.Resolve("Y'shtola", "en") is null, "Missing language must not change actor version");
        Check(resolver.Resolve("???", "ja") is null, "Unknown speaker must not use fallback voice");
        Check(resolver.Resolve("Unknown NPC", "ja", new Dictionary<string, string> { ["Unknown NPC"] = "TATARU" })?.Voice.Token == "TATARU", "Explicit alias");
        var catalogPath = Path.Combine(folder, "catalog.json"); catalog.Save(catalogPath);
        var library = new GameVoiceLibrary(); library.Load(catalogPath);
        Check(library.Resolve("Y'shtola", "yue", new Settings()).Language == "yue", "Translation target controls speech language");
        Check(library.Resolve("Y'shtola", "zh-TW", new Settings()).Transcript.Length == 0, "Cross-language clone must not use continuation transcript");
        var custom = new VoiceProfile { Speaker = "Y'shtola", ReferenceAudio = reference, Language = "en" };
        Check(ReferenceEquals(custom, library.Resolve("Y'shtola", "zh-TW", new Settings { Voices = [custom] })), "Manual voice override");

        var handler = new FakeHandler();
        using var speech = new SpeechClient(handler);
        var voice = new VoiceProfile { Speaker = "Tataru", ReferenceAudio = reference, Language = "yue" };
        var cache = Path.Combine(folder, "cache");
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 2, cache);
        Check(handler.Calls == 1 && handler.LastJson!.Contains("session_id"), "HTTP request carries dialogue identity");
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 3, cache);
        Check(handler.Calls == 1, "Repeated line uses disk cache");
        await File.AppendAllTextAsync(reference, "changed");
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 4, cache);
        Check(handler.Calls == 2, "Changed reference invalidates cached audio");
        handler.Block = true;
        using var cancel = new CancellationTokenSource();
        var pending = speech.Generate("http://127.0.0.1:8808", "new line", voice, cancel.Token, 5, cache);
        await handler.Started.Task;
        cancel.Cancel(); handler.Release.SetResult();
        try { await pending; throw new Exception("Canceled response reached playback"); }
        catch (OperationCanceledException) { }
        Check(Directory.GetFiles(cache, "*.wav").Length == 2, "Canceled response must not enter cache");

        var actual = GameVoiceLibrary.FindCatalog();
        if (actual.Length > 0)
        {
            var built = VoiceCatalog.Load(actual);
            Check(built.Voices.Length > 0, "Imported catalog contains speakers");
            Check(built.Voices.All(v => v.References.Length > 0), "Every indexed token has a reference");
            var real = new VoiceResolver(built, Path.GetDirectoryName(actual)!);
            Check(real.Resolve("Y'shtola", "ja") is not null, "Actual English name finds Japanese voice");
            Check(real.Resolve("ヤ・シュトラ", "ja") is not null, "Actual localized name finds Japanese voice");
            Console.WriteLine($"Catalog: {built.Voices.Length} tokens, {built.Voices.Sum(v => v.References.Length)} references.");
        }
        // Only delete this test's known temporary directory.
        Directory.Delete(folder, true);
        Console.WriteLine("PASS: actor aliases, ambiguity, language selection, custom overrides, audio cache, cancellation.");
    }

    public static async Task Live(string outputFolder)
    {
        var library = new GameVoiceLibrary(); library.Load(GameVoiceLibrary.FindCatalog());
        var voice = library.Resolve("Y'shtola", "yue", new Settings());
        var translator = new Translator();
        var translated = await translator.Translate("Good morning, my friend. Let us begin our adventure.", "en", "yue", CancellationToken.None);
        Check(!string.IsNullOrWhiteSpace(translated), "Live translation returned text");
        Console.WriteLine("Live translation: " + translated);
        Console.WriteLine("Matched reference: " + voice.ReferenceAudio);
        using var speech = new SpeechClient();
        await speech.WarmUp("http://127.0.0.1:8808", CancellationToken.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var wav = await speech.Generate("http://127.0.0.1:8808", translated, voice, CancellationToken.None, 1);
        Check(wav.Length > 1000, "Real model produced speech");
        Directory.CreateDirectory(outputFolder);
        await File.WriteAllBytesAsync(Path.Combine(outputFolder, "integrated-yshtola-yue.wav"), wav);
        Console.WriteLine($"PASS: Tataru translation → Japanese Y'shtola reference → Cantonese WAV, {watch.Elapsed.TotalSeconds:F2}s generation.");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls; public string? LastJson; public bool Block;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; LastJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Block) { Started.TrySetResult(); await Release.Task; }
            var wav = new byte[44]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        }
    }
}
