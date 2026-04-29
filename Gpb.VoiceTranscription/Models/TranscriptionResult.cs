using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Gpb.VoiceTranscription.Models
{
    /// <summary>
    /// Результат транскрибации аудиофайла
    /// </summary>
    public class TranscriptionResult
    {
        /// <summary>
        /// Путь к исходному аудиофайлу
        /// </summary>
        public string SourceFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Язык распознавания (например: "en", "ru", "auto")
        /// </summary>
        public string DetectedLanguage { get; set; } = "auto";

        /// <summary>
        /// Использованная модель Whisper (tiny/base/small/medium/large)
        /// </summary>
        public string ModelName { get; set; } = "small";

        /// <summary>
        /// Дата и время начала транскрибации
        /// </summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Дата и время завершения транскрибации
        /// </summary>
        public DateTime CompletedAt { get; set; }

        /// <summary>
        /// Общее время обработки в миллисекундах
        /// </summary>
        public double ProcessingTimeMs =>
            (CompletedAt - StartedAt).TotalMilliseconds;

        /// <summary>
        /// Список сегментов с таймкодами и текстом
        /// </summary>
        public List<TranscriptionSegment> Segments { get; set; } = new();

        /// <summary>
        /// Полный текст без форматирования (для поиска/копирования)
        /// </summary>
        [JsonIgnore]
        public string PlainText =>
            string.Join(" ", Segments.Select(s => s.Text.Trim()));

        /// <summary>
        /// Текст в формате Markdown с таймкодами
        /// </summary>
        public string ToMarkdown()
        {
            var sb = new StringBuilder();

            // Метаданные в комментариях YAML-style
            sb.AppendLine("---");
            sb.AppendLine($"source: \"{SourceFilePath}\"");
            sb.AppendLine($"language: {DetectedLanguage}");
            sb.AppendLine($"model: {ModelName}");
            sb.AppendLine($"processed: {StartedAt:yyyy-MM-ddTHH:mm:ssZ}");
            sb.AppendLine($"duration_ms: {ProcessingTimeMs}");
            sb.AppendLine("---");
            sb.AppendLine();

            // Заголовок
            sb.AppendLine($"# 🎙️ Транскрипция: {Path.GetFileName(SourceFilePath)}");
            sb.AppendLine();

            // Сегменты с таймкодами
            foreach (var segment in Segments)
            {
                sb.AppendLine(segment.ToMarkdownLine());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Текст в формате SRT для субтитров
        /// </summary>
        public string ToSrt()
        {
            var sb = new StringBuilder();
            int index = 1;

            foreach (var segment in Segments)
            {
                sb.AppendLine(index++.ToString());
                sb.AppendLine(segment.ToSrtTimestamp());
                sb.AppendLine(segment.Text.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Текст в формате JSON для дальнейшего анализа
        /// </summary>
        public string ToJson() =>
            System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

        /// <summary>
        /// Сохранить результат в файл
        /// </summary>
        public void SaveToFile(string outputPath, ExportFormat format = ExportFormat.Markdown)
        {
            CompletedAt = DateTime.UtcNow;

            string content = format switch
            {
                ExportFormat.Markdown => ToMarkdown(),
                ExportFormat.Srt => ToSrt(),
                ExportFormat.Json => ToJson(),
                ExportFormat.PlainText => PlainText,
                _ => ToMarkdown()
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, content, Encoding.UTF8);
        }
    }
}
