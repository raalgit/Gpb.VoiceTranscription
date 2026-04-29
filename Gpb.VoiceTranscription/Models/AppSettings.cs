using System.Configuration;

namespace Gpb.VoiceTranscription.Models
{
    public class AppSettings
    {
        public static string WhisperBaseUrl =>
            ConfigurationManager.AppSettings["WhisperBaseUrl"]
            ?? "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        public static int DownloadTimeoutMinutes
        {
            get
            {
                if (int.TryParse(ConfigurationManager.AppSettings["DownloadTimeoutMinutes"], out var minutes))
                    return minutes;
                return 10;
            }
        }
    }
}
