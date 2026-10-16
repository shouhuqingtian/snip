using System.IO;
using System.Text.Json;
using Snip.Core;
using Snip.Infrastructure;
namespace Snip.Windows.Checks;
internal static class NativeSpeechCheck
{
    public static int Run()
    {
        var results = new List<object>(); int missing = 0;
        try
        {
            foreach (string language in new[] { "en", "zh" })
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    ulong bytes = new WindowsTextSpeechService().SynthesizeSampleAsync(language, deadline.Token).WaitAsync(deadline.Token).GetAwaiter().GetResult();
                    if (bytes < 44) throw new InvalidOperationException("Windows synthesis returned no audio.");
                    results.Add(new { Language = language, Outcome = "Passed", AudioBytes = bytes });
                    Console.WriteLine($"PASS actual Windows {(language == "zh" ? "Chinese" : "English")} speech synthesis: {bytes} bytes; audible output requires an audio device and remains a manual check.");
                }
                catch (SpeechUnavailableException)
                {
                    missing++; results.Add(new { Language = language, Outcome = "Unrun", AudioBytes = 0UL });
                    Console.WriteLine($"UNRUN Windows {(language == "zh" ? "Chinese" : "English")} voice is not installed.");
                }
            }
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/speech-languages.json", JsonSerializer.Serialize(results));
            return missing == 0 ? 0 : 2;
        }
        catch (Exception error) { Console.WriteLine("FAIL native speech: " + error.GetType().Name); return 1; }
    }
}
