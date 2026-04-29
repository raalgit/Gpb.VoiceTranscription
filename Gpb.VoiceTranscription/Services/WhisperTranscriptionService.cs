using Gpb.VoiceTranscription.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const int ChunkDurationSeconds = 300; // 5 минут на чанк для оптимизации больших файлов

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
                    await DownloadModelAsync(_modelPath, GgmlType.Small);
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
            string modelName = "small",
            bool useChunking = true,
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

            // Проверяем длительность файла для оптимизации обработки больших файлов
            var duration = AudioConverter.GetAudioDuration(wavFilePath);

            if (useChunking && duration.TotalMinutes > 10)
            {
                // Для больших файлов используем чанковую обработку
                ProgressChanged?.Invoke($"⏱️ Длительность файла: {duration.TotalMinutes:F1} мин. Использую чанковую обработку...");
                return await TranscribeWithChunkingAsync(wavFilePath, modelName, cancellationToken);
            }

            // ✅ Создаём процессор с правильным обработчиком прогресса
            using var processor = _factory!.CreateBuilder()
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

        /// <summary>
        /// Транскрибация больших файлов через разбиение на чанки
        /// </summary>
        private async Task<TranscriptionResult> TranscribeWithChunkingAsync(
            string wavFilePath,
            string modelName,
            CancellationToken cancellationToken)
        {
            var allSegments = new List<TranscriptionSegment>();
            var chunkPaths = new List<string>();
            var globalStartTime = TimeSpan.Zero;

            try
            {
                // Разбиваем файл на чанки по 5 минут
                ProgressChanged?.Invoke($"🔪 Разбиение файла на чанки по {ChunkDurationSeconds / 60} мин...");
                chunkPaths = await AudioConverter.SplitAudioIntoChunksAsync(wavFilePath, ChunkDurationSeconds);

                ProgressChanged?.Invoke($"📊 Найдено {chunkPaths.Count} чанков. Начинаю обработку...");

                int processedChunks = 0;

                foreach (var chunkPath in chunkPaths)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    ProgressChanged?.Invoke($"🎙️ Обработка чанка {processedChunks + 1}/{chunkPaths.Count}...");

                    using var processor = _factory!.CreateBuilder()
                        .WithLanguage(_language)
                        .WithProgressHandler(progress =>
                        {
                            // Общий прогресс с учётом текущего чанка
                            var overallProgress = (processedChunks + progress) / chunkPaths.Count;
                            ProgressPercentChanged?.Invoke(overallProgress);
                        })
                        .Build();

                    using var fileStream = File.OpenRead(chunkPath);

                    await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
                    {
                        // Корректируем таймкоды относительно начала исходного файла
                        var adjustedStart = globalStartTime + TimeSpan.FromMilliseconds(segment.Start.TotalMilliseconds);
                        var adjustedEnd = globalStartTime + TimeSpan.FromMilliseconds(segment.End.TotalMilliseconds);

                        allSegments.Add(new TranscriptionSegment
                        {
                            Start = adjustedStart,
                            End = adjustedEnd,
                            Text = segment.Text.Trim()
                        });
                    }

                    processedChunks++;
                    globalStartTime += TimeSpan.FromSeconds(ChunkDurationSeconds);

                    // Удаляем временный файл чанка
                    if (File.Exists(chunkPath))
                        File.Delete(chunkPath);
                }

                ProgressChanged?.Invoke($"✅ Обработано {processedChunks} чанков");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ProgressChanged?.Invoke($"❌ Ошибка при обработке чанков: {ex.Message}");
                throw;
            }
            finally
            {
                // Очищаем оставшиеся временные файлы в случае ошибки
                foreach (var chunkPath in chunkPaths.Where(File.Exists))
                {
                    try { File.Delete(chunkPath); } catch { }
                }
            }

            return new TranscriptionResult
            {
                SourceFilePath = wavFilePath,
                ModelName = modelName,
                DetectedLanguage = _language,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Segments = allSegments.OrderBy(s => s.Start).ToList()
            };
        }

        public static async Task DownloadModelAsync(string fileName, GgmlType ggmlType)
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
