using Snip.Core;
using Snip.Infrastructure;
namespace Snip.Windows.Checks;
internal static class NativeSpeechCheck
{
    public static int Run()
    {
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ulong bytes=new WindowsTextSpeechService().SynthesizeEnglishSampleAsync(deadline.Token).GetAwaiter().GetResult();
            if(bytes<44)throw new InvalidOperationException("Windows synthesis returned no audio.");
            Console.WriteLine($"PASS actual Windows English speech synthesis: {bytes} bytes; audible output requires an audio device and remains a manual check.");return 0;
        }
        catch(SpeechUnavailableException){Console.WriteLine("UNRUN Windows English voice is not installed.");return 2;}
        catch(Exception error){Console.WriteLine("FAIL native speech: "+error.GetType().Name);return 1;}
    }
}
