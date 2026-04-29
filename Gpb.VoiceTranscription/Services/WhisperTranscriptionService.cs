using Gpb.VoiceTranscription.Helpers;
using Gpb.VoiceTranscription.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Gpb.VoiceTranscription.Services
{
    public class WhisperTranscriptionService : IDisposable
    {
        #region Fields

        private WhisperFactory? _factory;
        private readonly string _modelPath;
        private readonly string _language;
        private const int ChunkDurationSeconds = 300; // 5 минут на чанк для оптимизации больших файлов

        #endregion

        #region Events

        public event Action<string>? ProgressChanged;
        public event Action<float>? ProgressPercentChanged;

        #endregion

        #region ctor

        public WhisperTranscriptionService(string modelPath, string language = "auto")
        {
            _modelPath = modelPath;
            _language = language;
        }

        #endregion

        #region Methods

        public async Task<bool> InitializeAsync()
        {
            try
            {
                if (!File.Exists(_modelPath))
                {
                    ProgressChanged?.Invoke("📥 Модель не найдена");
                    return false;
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
            bool useChunkingForLargeSize = true,
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
            var duration = AudioConverterHelper.GetAudioDuration(wavFilePath);

            if (useChunkingForLargeSize && duration.TotalMinutes > 10)
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
            var rawSegments = new List<TranscriptionSegment>();
            await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
            {
                rawSegments.Add(new TranscriptionSegment
                {
                    Start = segment.Start,
                    End = segment.End,
                    Text = segment.Text.Trim()
                });
            }

            // Объединяем короткие сегменты в полноценные предложения
            result.Segments = MergeShortSegments(rawSegments);

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
                chunkPaths = await AudioConverterHelper.SplitAudioIntoChunksAsync(wavFilePath, ChunkDurationSeconds);

                ProgressChanged?.Invoke($"📊 Найдено {chunkPaths.Count} чанков. Начинаю обработку...");

                int processedChunks = 0;

                foreach (var chunkPath in chunkPaths)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new TaskCanceledException();

                    ProgressChanged?.Invoke($"🎙️ Обработка чанка {processedChunks + 1}/{chunkPaths.Count}...");

                    using var processor = _factory!.CreateBuilder()
                        .WithLanguage(_language)
                        .WithProgressHandler(progress =>
                        {
                            // Общий прогресс с учётом текущего чанка
                            var overallProgress = (100 * processedChunks + progress) / chunkPaths.Count;
                            ProgressPercentChanged?.Invoke(overallProgress);
                        })
                        .Build();

                    using var fileStream = File.OpenRead(chunkPath);

                    await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new TaskCanceledException();

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
                Segments = MergeShortSegments(allSegments.OrderBy(s => s.Start).ToList())
            };
        }

        /// <summary>
        /// Объединяет короткие сегменты в более длинные предложения.
        /// Whisper по умолчанию разбивает текст на сегменты 1-5 секунд, что приводит к разрыву предложений.
        /// Этот метод объединяет сегменты, если:
        /// - Они идут подряд (без больших пауз)
        /// - Общий размер объединённого текста не превышает разумный предел
        /// - Предыдущий сегмент не заканчивается на точку/вопрос/восклицание
        /// </summary>
        private List<TranscriptionSegment> MergeShortSegments(List<TranscriptionSegment> segments)
        {
            if (segments.Count == 0)
                return segments;

            var mergedSegments = new List<TranscriptionSegment>();
            var currentSegment = segments[0];

            // Максимальная длительность сегмента в секундах (объединяем до ~15 секунд)
            const double maxSegmentDuration = 15.0;
            // Максимальная пауза между сегментами для объединения (в миллисекундах)
            const long maxPauseMs = 800;

            for (int i = 1; i < segments.Count; i++)
            {
                var nextSegment = segments[i];
                var pauseMs = (nextSegment.Start - currentSegment.End).TotalMilliseconds;
                var combinedDuration = (nextSegment.End - currentSegment.Start).TotalSeconds;

                // Проверяем, можно ли объединить сегменты
                bool shouldMerge =
                    pauseMs <= maxPauseMs &&  // Пауза небольшая
                    combinedDuration <= maxSegmentDuration &&  // Общая длительность в пределах
                    !currentSegment.Text.EndsWith(".") &&
                    !currentSegment.Text.EndsWith("?") &&
                    !currentSegment.Text.EndsWith("!") &&
                    !currentSegment.Text.EndsWith("…");

                if (shouldMerge)
                {
                    // Объединяем сегменты
                    currentSegment = new TranscriptionSegment
                    {
                        Start = currentSegment.Start,
                        End = nextSegment.End,
                        Text = currentSegment.Text + " " + nextSegment.Text
                    };
                }
                else
                {
                    // Сохраняем текущий сегмент и начинаем новый
                    mergedSegments.Add(currentSegment);
                    currentSegment = nextSegment;
                }
            }

            // Добавляем последний сегмент
            mergedSegments.Add(currentSegment);

            return mergedSegments;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            _factory?.Dispose();
        }

        #endregion
    }
}
