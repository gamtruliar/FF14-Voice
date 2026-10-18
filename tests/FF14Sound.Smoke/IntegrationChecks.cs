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
        Check(GameAccess.IsAccessDenied(new System.ComponentModel.Win32Exception(5)), "Win32 access denial detected");
        Check(GameAccess.IsAccessDenied(new InvalidOperationException("wrapper", new UnauthorizedAccessException())), "Nested access denial detected");
        Check(!GameAccess.IsAccessDenied(new System.ComponentModel.Win32Exception(2)), "Missing file must not suggest elevation");
        Check(GameAccess.Describe(new System.ComponentModel.Win32Exception(5), "連接 FF14", false).Contains("UAC"), "Non-elevated failure gives recovery action");
        Check(!GameAccess.Describe(new System.ComponentModel.Win32Exception(5), "連接 FF14", true).Contains("再連接"), "Elevated failure must not loop elevation");
        var folder = Path.Combine(Path.GetTempPath(), "ff14sound-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        GlossaryChecks.Run(folder);
        TranslationOutputChecks.Run();
        await VoiceSeedChecks.Run(folder);
        await SpeechCacheChecks.Run(folder);
        await BatchChecks.Run(folder);
        await StreamingChecks.Run(folder);
        await OllamaChecks.Run();
        await GeminiChecks.Run(folder);
        var preferencesPath = Path.Combine(folder, "preferences.json");
        var preferences = new Settings { SourceLanguage = "ja", TargetLanguage = "yue", SpeechMode = "original", MainTopmost = true,
            ReferenceLanguage = "en", AutoSpeak = false, Subtitles = new() { Enabled = true, ContentMode = "both", FontSize = 34, BackgroundOpacity = 0.6, Left = -1400, Top = 700, Width = 900, Height = 200, Compact = true } };
        preferences.Save(preferencesPath);
        var restored = Settings.Load(preferencesPath);
        Check(restored.SourceLanguage == "ja" && restored.TargetLanguage == "yue" && restored.SpeechMode == "original" && restored.MainTopmost && !restored.AutoSpeak && restored.ReferenceLanguage == "en", "Playback and language settings persist");
        Check(restored.Subtitles.Enabled && restored.Subtitles.ContentMode == "both" && restored.Subtitles.Left == -1400 && restored.Subtitles.FontSize == 34 && restored.Subtitles.Compact, "Subtitle appearance and placement persist");
        await File.WriteAllTextAsync(preferencesPath, "{\"SourceLanguage\":\"ja\"}");
        Check(Settings.Load(preferencesPath).SpeechMode == "translated" && !Settings.Load(preferencesPath).Subtitles.Enabled, "Old settings migrate with safe defaults");
        var selected = SpeechSelection.Choose("original", "原文", "翻譯", "ja", "yue");
        Check(selected.Text == "原文" && selected.Language == "ja", "Original dubbing uses source text and language");
        var adjusted = selected.Apply(new VoiceProfile { Language = "yue", Transcript = "old continuation", Control = "calm" });
        Check(adjusted.Language == "ja" && adjusted.Transcript == "" && adjusted.Control == "calm", "Original mode overrides custom target language without incompatible transcript");
        Check(SpeechSelection.Choose("translated", "原文", "翻譯", "ja", "yue").Language == "yue", "Translated dubbing keeps target language");
        var translationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speechStarted = false;
        var concurrent = SpeechWorkflow.Run(() => translationGate.Task, () => { speechStarted = true; return Task.CompletedTask; }, true);
        Check(speechStarted && !concurrent.IsCompleted, "Original audio starts before translation finishes");
        translationGate.SetResult(); await concurrent;
        speechStarted = false;
        try { await SpeechWorkflow.Run(() => Task.FromException(new IOException("translation unavailable")), () => { speechStarted = true; return Task.CompletedTask; }, true); }
        catch (IOException) { }
        Check(speechStarted, "Translation failure does not prevent original audio");
        speechStarted = false;
        try { await SpeechWorkflow.Run(() => Task.FromException(new IOException("translation unavailable")), () => { speechStarted = true; return Task.CompletedTask; }, false); }
        catch (IOException) { }
        Check(!speechStarted, "Translated mode never speaks stale text after a translation failure");
        var pendingTranslation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translatedAfterManualPlay = false;
        var playbackVersion = 1;
        var queuedVersion = playbackVersion;
        var queuedSpeech = SpeechWorkflow.Run(async () => { await pendingTranslation.Task; translatedAfterManualPlay = true; },
            () => { speechStarted = true; return Task.CompletedTask; }, false,
            canSpeak: () => playbackVersion == queuedVersion);
        playbackVersion++; // User chooses original audio while the translation is still pending.
        pendingTranslation.SetResult(); await queuedSpeech;
        Check(translatedAfterManualPlay && !speechStarted, "Manual playback keeps translation but supersedes queued auto speech");
        var placement = SubtitleWindow.Place(restored.Subtitles, new System.Windows.Rect(0, 0, 1920, 1080));
        Check(placement.Left >= 0 && placement.Right <= 1920 && placement.Bottom <= 1080, "Restore subtitles onto current screen after a monitor is removed");
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
        Check(library.Resolve("Y'shtola", "ja", new Settings()).EffectiveSeed == library.Resolve("ヤ・シュトラ", "yue", new Settings()).EffectiveSeed, "Cloned actor aliases share a fixed seed");
        Check(library.Resolve("Y'shtola", "yue", new Settings()).Language == "yue", "Translation target controls speech language");
        Check(library.Resolve("Y'shtola", "zh-TW", new Settings()).Transcript.Length == 0, "Cross-language clone must not use continuation transcript");
        var custom = new VoiceProfile { Speaker = "Y'shtola", ReferenceAudio = reference, Language = "en" };
        Check(ReferenceEquals(custom, library.Resolve("Y'shtola", "zh-TW", new Settings { Voices = [custom] })), "Manual voice override");
        await File.WriteAllTextAsync(preferencesPath, "{\"DefaultReferenceAudio\":\"legacy-female.wav\"}");
        var defaults = Settings.Load(preferencesPath);
        var fallback = library.Resolve("イベント案内窓口", "yue", defaults, "friendly guide");
        Check(fallback.IsFallback && fallback.ReferenceAudio.Length == 0 && fallback.Language == "yue" && fallback.Control == "friendly guide", "Unmatched NPC ignores legacy default reference and uses text-only direction");
        Check(fallback.Transcript.Length == 0, "Text-only fallback has no continuation transcript");
        Check(library.Resolve("Shared", "yue", defaults).IsFallback, "Ambiguous name also falls back");
        Check(library.Resolve("Y'shtola", "yue", defaults).IsFallback == false, "Matched actor takes priority over default");
        Check(new GameVoiceLibrary().Resolve("Unknown", "en", new Settings()).ReferenceAudio.Length == 0, "Text-only default works without catalog or bundled audio");

        var mappingSettings = new Settings { SourceLanguage = "ja", SpeakerAliases = new() { ["イベント案内窓口"] = "TATARU" } };
        var automatic = library.ResolveAssignment("Y'shtola", "yue", mappingSettings);
        Check(automatic.Voice == "YSHTOLA" && automatic.Basis.Contains("自動") && automatic.ReferenceLanguage == "ja", "Mapping describes the actual automatic reference");
        var manual = library.ResolveAssignment("イベント案内窓口", "yue", mappingSettings);
        Check(manual.Voice == "TATARU" && manual.Basis.Contains("手動"), "Explicit mapping is identified");
        mappingSettings.Voices.Add(new() { Speaker = "イベント案内窓口", ReferenceAudio = reference });
        Check(library.ResolveAssignment("イベント案内窓口", "yue", mappingSettings).Basis.Contains("自訂"), "Mapping displays custom profile priority over manual alias");
        mappingSettings.Voices.Clear(); mappingSettings.ReferenceLanguage = "en";
        Check(library.ResolveAssignment("イベント案内窓口", "yue", mappingSettings).Voice.StartsWith("default"), "Unavailable reference language displays default instead of stale token");
        mappingSettings.ReferenceLanguage = "ja";
        var mappedRow = new NpcVoiceEntry { Japanese = "イベント案内窓口", English = "event attendant" };
        library.DescribeRow(mappedRow, mappingSettings);
        Check(mappedRow.VoiceMapping.Contains("TATARU") && mappedRow.MappingSpeaker == mappedRow.Japanese && mappedRow.MappedReference == reference, "NPC table mapping uses current display-name language");
        mappingSettings.SourceLanguage = "en"; library.DescribeRow(mappedRow, mappingSettings);
        Check(mappedRow.VoiceMapping.StartsWith("default"), "Japanese manual alias does not falsely apply to English name");

        var gerolt = new NpcVoiceEntry { English = "Gerolt", Japanese = "ゲロルト" };
        NpcVoiceIndex.SetSuggestedDescription(gerolt);
        Check(gerolt.DescriptionBasis.StartsWith("已知身份") && gerolt.DescriptionSource.StartsWith("https://") && gerolt.VoiceDescription.Contains("低音"), "Known NPC has sourced identity and authored voice direction");
        var injured = new NpcVoiceEntry { English = "wounded guard" };
        NpcVoiceIndex.SetSuggestedDescription(injured);
        Check(injured.DescriptionBasis.Contains("守衛") && injured.VoiceDescription.Contains("吃力"), "Role and explicit injury both inform direction");
        var unknown = new NpcVoiceEntry { English = "Edgard" };
        NpcVoiceIndex.SetSuggestedDescription(unknown);
        var unknownCopy = unknown.Copy(); NpcVoiceIndex.SetSuggestedDescription(unknownCopy);
        Check(unknown.DescriptionBasis.Contains("身份未確認") && unknown.VoiceDescription == unknownCopy.VoiceDescription, "Unknown proper names get stable creative direction without substring role guesses");
        var femaleChild = new NpcVoiceEntry { English = "test resident", NpcIds = [11], Appearances = [new(11, 4, 1, 4, 8)] };
        NpcVoiceIndex.SetSuggestedDescription(femaleChild);
        Check(femaleChild.VoiceDescription.StartsWith("幼年／少年女聲。") && femaleChild.VoiceTraitsBasis.Contains("BodyType=4"), "Female young model reaches TTS description with provenance");
        var oldMale = new NpcVoiceEntry { English = "test resident", NpcIds = [12], Appearances = [new(12, 1, 0, 3, 1)] };
        NpcVoiceIndex.SetSuggestedDescription(oldMale);
        Check(oldMale.VoiceDescription.StartsWith("老年男聲。"), "Older male model gets old male direction");
        var lalafell = new NpcVoiceEntry { English = "test resident", NpcIds = [13], Appearances = [new(13, 3, 1, 1, 5)] };
        NpcVoiceIndex.SetSuggestedDescription(lalafell);
        Check(lalafell.VoiceAge == "成年", "Small race must not be treated as a child");
        var mixed = new NpcVoiceEntry { English = "shared resident", NpcIds = [11,12], Appearances = [new(11,4,1,4,8),new(12,1,0,3,1)] };
        NpcVoiceIndex.SetSuggestedDescription(mixed);
        Check(mixed.VoiceTraitsBasis.Contains("同名 NPC 有不同性別") && mixed.VoiceTraitsBasis.Contains("暫採成年"), "Mixed name group does not assert one demographic");
        Check(unknown.VoiceGender.Length > 0 && unknown.VoiceAge.Length > 0 && unknown.VoiceTraitsBasis.Contains("創作選角"), "Missing traits remain populated and explicitly creative");
        var actualTraitsPath = Path.Combine(Path.GetDirectoryName(GameVoiceLibrary.FindCatalog()) ?? "", "npc-voice-index.json");
        if (File.Exists(actualTraitsPath))
        {
            var actualNpcs = JsonSerializer.Deserialize<NpcVoiceEntry[]>(await File.ReadAllTextAsync(actualTraitsPath))!;
            var eugennoix = actualNpcs.Single(r => r.NpcIds.Contains(1006433u));
            Check(eugennoix.VoiceDescription.StartsWith("成年男聲。") && eugennoix.Appearances.Any(a => a.NpcId == 1006433 && a.Race == 2 && a.Gender == 0), "Screenshot NPC Eugennoix is grounded in client appearance fields");
            Check(actualNpcs.Where(r => r.ReferenceTokens.Length == 0).All(r => r.VoiceDescription.StartsWith(r.VoiceAge + r.VoiceGender + "。") && r.VoiceAge.Length > 0 && r.VoiceGender.Length > 0), "All unmatched descriptions contain voice age and gender");
        }

        var npc = new NpcVoiceEntry { Key = "npc:1", NpcIds = [1, 2], Japanese = "イベント案内窓口", English = "event attendant" };
        NpcVoiceIndex.SetSuggestedDescription(npc);
        var indexPath = Path.Combine(folder, "npc-voice-index.json");
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(new[] { npc }));
        var registryFile = Path.Combine(folder, "npc-notes.json");
        var registry = new NpcVoiceRegistry(registryFile); registry.Load(indexPath);
        registry.Observe("イベント案内窓口", "ja");
        Check(registry.Rows.Count() == 1, "Encounter reuses indexed NPC instead of duplicating it");
        var legacy = npc.Copy(); legacy.VoiceDescription = "自然、清晰、親切，中等語速，平穩敘述。";
        legacy.DescriptionVersion = 0; legacy.DescriptionBasis = "建議演繹（按名稱／通用模板），非官方音色；性別、年齡未推斷";
        legacy.CvMark = "有原聲待配對"; legacy.Notes = "keep observation"; legacy.LastSeen = DateTimeOffset.Now;
        await File.WriteAllTextAsync(registryFile, JsonSerializer.Serialize(new[] { legacy }));
        registry.Load(indexPath);
        Check(registry.Find(npc.Japanese)!.VoiceDescription == npc.VoiceDescription && registry.Find(npc.Japanese)!.Notes == legacy.Notes, "Legacy observed templates refresh while preserving notes");
        var edit = registry.Find("event attendant")!.Copy();
        edit.VoiceDescription = "cheerful and clear"; edit.CvMark = "確認無原聲"; edit.Notes = "test, with comma";
        registry.ApplyEdits([edit]);
        var reloaded = new NpcVoiceRegistry(registryFile); reloaded.Load(indexPath);
        Check(reloaded.DescriptionFor("イベント案内窓口", "neutral") == "cheerful and clear", "Edited description survives restart and localized names");
        Check(reloaded.Find("event attendant")?.CvMark == "確認無原聲", "Manual CV mark persisted");
        Check(reloaded.Find("event attendant")?.LastSeen is not null, "Encounter recorded");
        reloaded.Observe("New guard", "en");
        Check(reloaded.Rows.Count() == 2 && reloaded.Find("New guard")!.OriginalStatus.Contains("未確認"), "New NPC recorded without falsely asserting no CV");
        var csv = Path.Combine(folder, "table.csv"); NpcVoiceIndex.ExportCsv(csv, reloaded.Rows);
        Check((await File.ReadAllTextAsync(csv)).Contains("\"test, with comma\""), "CSV preserves commas in notes");
        Check((await File.ReadAllTextAsync(csv)).Contains("Voice mapping,Mapping speaker,Mapping basis,Reference audio,角色身份來源"), "CSV includes mapping provenance and identity source");

        var handler = new FakeHandler();
        var noCloneHandler = new FakeHandler();
        using (var noCloneSpeech = new SpeechClient(noCloneHandler))
        {
            var defaultCache = Path.Combine(folder, "default-cache");
            // Even a stale fallback object cannot accidentally resume cloning.
            fallback.ReferenceAudio = "missing-old-default.wav"; fallback.Transcript = "old transcript";
            await noCloneSpeech.Generate("http://127.0.0.1:8808", "你好", fallback, CancellationToken.None, 1, defaultCache);
            using var request = JsonDocument.Parse(noCloneHandler.LastJson!);
            Check(request.RootElement.GetProperty("reference_audio").ValueKind == JsonValueKind.Null && request.RootElement.GetProperty("transcript").GetString() == "", "Default sends no reference or transcript to API");
            Check(request.RootElement.GetProperty("control").GetString() == "friendly guide", "Default passes voice direction");
            await noCloneSpeech.Generate("http://127.0.0.1:8808", "你好", fallback, CancellationToken.None, 2, defaultCache);
            Check(noCloneHandler.Calls == 1, "Text-only cache works without reading a WAV reference");
            fallback.Control = "成年男聲";
            await noCloneSpeech.Generate("http://127.0.0.1:8808", "你好", fallback, CancellationToken.None, 3, defaultCache);
            Check(noCloneHandler.Calls == 2, "Changed text-only description invalidates cache");
        }
        using var speech = new SpeechClient(handler);
        var voice = new VoiceProfile { Speaker = "Tataru", ReferenceAudio = reference, Language = "yue" };
        var cache = Path.Combine(folder, "cache");
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 2, cache);
        Check(handler.Calls == 1 && handler.LastJson!.Contains("session_id"), "HTTP request carries dialogue identity");
        Check(JsonDocument.Parse(handler.LastJson!).RootElement.GetProperty("seed").GetUInt32() == voice.EffectiveSeed, "WAV generation sends character seed");
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 3, cache);
        Check(handler.Calls == 1, "Repeated line uses disk cache");
        await File.AppendAllTextAsync(reference, "changed");
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 4, cache);
        Check(handler.Calls == 2, "Changed reference invalidates cached audio");
        voice.Control = "different voice description";
        await speech.Generate("http://127.0.0.1:8808", "你好", voice, CancellationToken.None, 5, cache);
        Check(handler.Calls == 3, "Changed NPC description invalidates cached audio");
        handler.Block = true;
        using var cancel = new CancellationTokenSource();
        var pending = speech.Generate("http://127.0.0.1:8808", "new line", voice, cancel.Token, 6, cache);
        await handler.Started.Task;
        cancel.Cancel(); handler.Release.SetResult();
        try { await pending; throw new Exception("Canceled response reached playback"); }
        catch (OperationCanceledException) { }
        Check(Directory.GetFiles(cache, "*.wav").Length == 3, "Canceled response must not enter cache");

        var actual = GameVoiceLibrary.FindCatalog();
        if (actual.Length > 0)
        {
            var built = VoiceCatalog.Load(actual);
            Check(built.Voices.Length > 0, "Imported catalog contains speakers");
            Check(built.Voices.All(v => v.References.Length > 0), "Every indexed token has a reference");
            var real = new VoiceResolver(built, Path.GetDirectoryName(actual)!);
            Check(real.Resolve("Y'shtola", "ja") is not null, "Actual English name finds Japanese voice");
            Check(real.Resolve("ヤ・シュトラ", "ja") is not null, "Actual localized name finds Japanese voice");
            var evidencePath = Path.Combine(Path.GetDirectoryName(actual)!, "voice-name-candidates.json");
            if (File.Exists(evidencePath))
            {
                var candidates = JsonSerializer.Deserialize<VoiceNameCandidate[]>(File.ReadAllText(evidencePath))!;
                Check(real.Resolve("スサノオ", "ja")?.Voice.Token == "SUSANO", "Battle NPC exact alias resolves to Susano");
                foreach (var candidate in candidates.Where(c => c.Applied))
                    Check(candidate.Names.All(built.Voices.Single(v => v.Token == candidate.Token).Names.Contains), "Applied aliases retained with evidence");
                var scoped = candidates.Single(c => c.Token == "SOLDIER03608" && c.Quest.Contains("LOC_ACTOR_SOLDIER"));
                Check(!scoped.Applied && scoped.RowIds.Contains(1029397u), "Quest-scoped wounded guard candidate is recorded without global assignment");
                Check(!built.Voices.Single(v => v.Token == scoped.Token).Names.Intersect(scoped.Names).Any(), "Quest candidate must not silently become a global alias");
            }
            Console.WriteLine($"Catalog: {built.Voices.Length} tokens, {built.Voices.Sum(v => v.References.Length)} references.");
        }
        // Only delete this test's known temporary directory.
        Directory.Delete(folder, true);
        Console.WriteLine("PASS: settings persistence/migration, original/translated speech sequencing, subtitle placement, actor matching, fallback, NPC table, cache, cancellation.");
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

    public static async Task LiveFallback(string outputFolder)
    {
        var library = new GameVoiceLibrary();
        var catalog = GameVoiceLibrary.FindCatalog(); if (catalog.Length > 0) library.Load(catalog);
        var registry = new NpcVoiceRegistry(Path.Combine(outputFolder, "unused-live-registry.json"));
        registry.Load(Path.Combine(Path.GetDirectoryName(catalog)!, "npc-voice-index.json"));
        var voice = library.Resolve("ウジェノア", "ja", new Settings(), registry.DescriptionFor("ウジェノア", "成年男聲，沉穩。"));
        Check(voice.IsFallback, "Live test must exercise the default voice");
        Check(voice.ReferenceAudio.Length == 0, "Live default must not clone any reference");
        using var speech = new SpeechClient();
        await speech.WarmUp("http://127.0.0.1:8808", CancellationToken.None);
        var wav = await speech.Generate("http://127.0.0.1:8808", "異端者め、スチールヴィジルが再建されぬよう、ドラゴン族と協力し、見張っていたな。", voice, CancellationToken.None, 1);
        await File.WriteAllBytesAsync(Path.Combine(outputFolder, "default-no-clone-eugennoix-ja.wav"), wav);
        voice.Language = "yue";
        wav = await speech.Generate("http://127.0.0.1:8808", "你哋呢班異端，唔好畀鋼鐵守夜團重建。", voice, CancellationToken.None, 2);
        await File.WriteAllBytesAsync(Path.Combine(outputFolder, "default-no-clone-eugennoix-yue.wav"), wav);
        Console.WriteLine("PASS: Eugennoix description → text-only Japanese and Cantonese WAVs, no reference supplied.");
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
            var wav = new byte[100]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        }
    }
}
