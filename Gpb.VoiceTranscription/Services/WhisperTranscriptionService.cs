using Gpb.VoiceTranscription.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace Gpb.VoiceTranscription.Services
{
    public class WhisperTranscriptionService : IDisposable
    {
        private WhisperFactory? _factory;
        private readonly string _modelPath;
        private readonly string _language;

        public event Action<string>? ProgressChanged;
        public event Action<float>? ProgressPercentChanged; // ✅ float вместо WhisperProgress

        public WhisperTranscriptionService(string modelPath, string language = "auto")
        {
            _modelPath = modelPath;
            _language = language;
        }

        public async Task<bool> InitializeAsync()
        {
            try
            {
                if (!File.Exists(_modelPath))
                {
                    ProgressChanged?.Invoke("📥 Модель не найдена. Начинаю загрузку...");
                    await DownloadModelAsync(_modelPath, GgmlType.Base);
                }

                _factory = WhisperFactory.FromPath(_modelPath);
                ProgressChanged?.Invoke("✅ Модель загружена и готова к работе!");
                return true;
            }
            catch (Exception ex)
            {
                ProgressChanged?.Invoke($"❌ Ошибка инициализации: {ex.Message}");
                return false;
            }
        }

        public async Task<TranscriptionResult> TranscribeAsync(
            string wavFilePath,
            string modelName = "base",
            CancellationToken cancellationToken = default)
        {
            if (_factory == null)
                throw new InvalidOperationException("Фабрика не инициализирована. Вызовите InitializeAsync()");
            if (!File.Exists(wavFilePath))
                throw new FileNotFoundException("WAV-файл не найден", wavFilePath);

            var result = new TranscriptionResult
            {
                SourceFilePath = wavFilePath,
                ModelName = modelName,
                DetectedLanguage = _language,
                StartedAt = DateTime.UtcNow
            };

            // ✅ Создаём процессор с правильным обработчиком прогресса
            using var processor = _factory.CreateBuilder()
                .WithLanguage(_language)
                .WithProgressHandler(progress =>
                {
                    // progress: float от 0.0 до 1.0
                    ProgressPercentChanged?.Invoke(progress);
                })
                .Build();

            using var fileStream = File.OpenRead(wavFilePath);

            // ✅ Асинхронная итерация по сегментам
            await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
            {
                result.Segments.Add(new TranscriptionSegment
                {
                    Start = segment.Start,
                    End = segment.End,
                    Text = segment.Text.Trim()
                });
            }

            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        private static async Task DownloadModelAsync(string fileName, GgmlType ggmlType)
        {
            // ✅ Прямой вызов статического метода
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType);

            // File.Create безопаснее: создаёт файл заново или перезаписывает
            using var fileWriter = File.Create(fileName);
            await modelStream.CopyToAsync(fileWriter);
        }

        public void Dispose()
        {
            _factory?.Dispose();
        }
    }
}
