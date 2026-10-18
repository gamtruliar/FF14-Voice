using System.Reflection;
using System.Windows.Controls;
using FF14Sound;

internal static class SubtitlePlaybackChecks
{
    // The caller supplies an indexed native-voiced line, so real button routing can
    // be exercised without a TTS server or audio device.
    public static void Run(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var run = typeof(MainWindow).GetMethod("Run", flags)!;
        var runSpeech = typeof(MainWindow).GetMethod("RunSpeech", flags)!;
        var playSubtitle = typeof(MainWindow).GetMethod("PlaySubtitle", flags)!;
        var generation = typeof(MainWindow).GetField("generation", flags)!;
        var mode = (ComboBox)window.FindName("SpeechMode");
        var savedMode = mode.SelectedValue;

        var translationGate = new TaskCompletionSource();
        var audioGate = new TaskCompletionSource();
        CancellationToken translationToken = default, audioToken = default;
        Func<CancellationToken, int, Task> translate = (token, _) => { translationToken = token; return translationGate.Task; };
        Func<CancellationToken, int, Task> speak = (token, _) => { audioToken = token; return audioGate.Task; };
        var translation = (Task)run.Invoke(window, [translate])!;
        var originalGeneration = generation.GetValue(window);
        var previousAudio = (Task)runSpeech.Invoke(window, [speak])!;

        foreach (var requestedMode in new[] { "original", "translated" })
        {
            var click = (Task<string>)playSubtitle.Invoke(window, [requestedMode])!;
            if (!click.IsCompletedSuccessfully || !click.Result.Contains("交由 GAME"))
                throw new Exception("Subtitle button failed to reach the shared native-audio guard");
            if (translationToken.IsCancellationRequested || translation.IsCompleted || !Equals(originalGeneration, generation.GetValue(window)))
                throw new Exception("Subtitle playback cancelled or invalidated the pending translation");
            if (!Equals(savedMode, mode.SelectedValue)) throw new Exception("Subtitle button changed the saved speech mode");
        }
        if (!audioToken.IsCancellationRequested) throw new Exception("Subtitle playback failed to cancel previous audio");
        audioGate.SetCanceled(audioToken);
        translationGate.SetResult();
        if (!translation.IsCompletedSuccessfully || !previousAudio.IsCompletedSuccessfully)
            throw new Exception("Translation/audio did not finish independently after a manual playback choice");

        // New dialogue still uses Run, which must invalidate both old jobs.
        translationGate = new(); audioGate = new();
        translation = (Task)run.Invoke(window, [translate])!;
        previousAudio = (Task)runSpeech.Invoke(window, [speak])!;
        Func<CancellationToken, int, Task> nextLine = (_, _) => Task.CompletedTask;
        ((Task)run.Invoke(window, [nextLine])!).GetAwaiter().GetResult();
        if (!translationToken.IsCancellationRequested || !audioToken.IsCancellationRequested)
            throw new Exception("New dialogue did not cancel both old translation and audio");
        translationGate.SetCanceled(translationToken); audioGate.SetCanceled(audioToken);
        if (!translation.IsCompletedSuccessfully || !previousAudio.IsCompletedSuccessfully)
            throw new Exception("Cancelled dialogue tasks did not finish cleanly");
        Console.WriteLine("PASS: subtitle playback preserves pending translation, replaces audio, keeps mode; new dialogue cancels both");
    }
}
