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

        #region Summarization Settings

        /// <summary>
        /// API ключ для сервиса суммаризации
        /// </summary>
        public static string SummarizationApiKey =>
            ConfigurationManager.AppSettings["SummarizationApiKey"] ?? string.Empty;

        /// <summary>
        /// URL endpoint для суммаризации (OpenAI compatible API)
        /// </summary>
        public static string SummarizationEndpoint =>
            ConfigurationManager.AppSettings["SummarizationEndpoint"]
            ?? "https://api.openai.com/v1/chat/completions";

        /// <summary>
        /// Модель для суммаризации (читается из конфига)
        /// </summary>
        public static string SummarizationModel =>
            ConfigurationManager.AppSettings["SummarizationModel"] ?? "gpt-3.5-turbo";

        /// <summary>
        /// Максимальная длина текста для суммаризации (символов)
        /// </summary>
        public static int SummarizationMaxTextLength
        {
            get
            {
                if (int.TryParse(ConfigurationManager.AppSettings["SummarizationMaxTextLength"], out var length))
                    return length;
                return 15000;
            }
        }

        /// <summary>
        /// Включить суммаризацию по умолчанию
        /// </summary>
        public static bool EnableSummarization
        {
            get
            {
                if (bool.TryParse(ConfigurationManager.AppSettings["EnableSummarization"], out var enabled))
                    return enabled;
                return false;
            }
        }

        #endregion
    }
}
