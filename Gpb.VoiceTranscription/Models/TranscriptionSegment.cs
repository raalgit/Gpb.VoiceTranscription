using System;

namespace Gpb.VoiceTranscription.Models
{
    /// <summary>
    /// Отдельный сегмент транскрибации с таймкодами
    /// </summary>
    public class TranscriptionSegment
    {
        /// <summary>
        /// Время начала сегмента
        /// </summary>
        public TimeSpan Start { get; set; }

        /// <summary>
        /// Время окончания сегмента
        /// </summary>
        public TimeSpan End { get; set; }

        /// <summary>
        /// Распознанный текст сегмента
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Уверенность модели (0.0 - 1.0), если доступно
        /// </summary>
        public float? Confidence { get; set; }

        /// <summary>
        /// ID спикера (для будущего расширения с diarization)
        /// </summary>
        public int? SpeakerId { get; set; }

        /// <summary>
        /// Форматирование сегмента для Markdown
        /// </summary>
        public string ToMarkdownLine()
        {
            var timeStr = $"[{Start:mm\\:ss}.{Start.Milliseconds:D3} – {End:mm\\:ss}.{End.Milliseconds:D3}]";
            var confidenceStr = Confidence.HasValue ? $" *(conf: {Confidence.Value:P0})*" : "";
            return $"> {timeStr} **{Text.Trim()}**{confidenceStr}";
        }

        /// <summary>
        /// Форматирование таймкода для SRT
        /// </summary>
        public string ToSrtTimestamp()
        {
            string FormatTime(TimeSpan ts) =>
                $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";

            return $"{FormatTime(Start)} --> {FormatTime(End)}";
        }
    }
}
