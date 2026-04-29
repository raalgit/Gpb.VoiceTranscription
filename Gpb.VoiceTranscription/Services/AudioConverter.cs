using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Gpb.VoiceTranscription.Services
{
    public static class AudioConverter
    {
        /// <summary>
        /// Проверяет, является ли файл поддерживаемым аудиоформатом (wav, mp3, mp4)
        /// </summary>
        public static bool IsSupportedAudioFormat(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension == ".wav" || extension == ".mp3" || extension == ".mp4" || extension == ".m4a";
        }

        public static bool IsWhisperCompatible(string filePath)
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                // Whisper требует: 16kHz, 16-bit PCM, Mono
                return reader.WaveFormat.SampleRate == 16000 &&
                       reader.WaveFormat.BitsPerSample == 16 &&
                       reader.WaveFormat.Channels == 1 &&
                       reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm;
            }
            catch { return false; }
        }

        /// <summary>
        /// Конвертирует любой поддерживаемый аудиофайл (WAV, MP3, MP4) в формат Whisper (16kHz Mono WAV)
        /// </summary>
        public static async Task ConvertToWhisperFormatAsync(string inputPath, string outputPath)
        {
            await Task.Run(() =>
            {
                // 1. Декодируем входной файл в IEEE Float (32-bit)
                // AudioFileReader автоматически декодирует MP3, MP4, WAV и другие форматы
                using var reader = new AudioFileReader(inputPath);
                ISampleProvider sampleProvider = reader;

                // 2. Сведение в Mono (если стерео или многоканал)
                if (reader.WaveFormat.Channels > 1)
                {
                    var monoProvider = new StereoToMonoSampleProvider(sampleProvider)
                    {
                        LeftVolume = 0.5f,
                        RightVolume = 0.5f
                    };
                    sampleProvider = monoProvider;
                }

                // 3. Ресемплинг до 16kHz (чистый C#, без зависимости от Windows ACM/MF)
                if (reader.WaveFormat.SampleRate != 16000)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
                }

                // 4. Конвертация IEEE Float (32-bit) → PCM (16-bit) для записи в WAV
                var waveProvider = new SampleToWaveProvider16(sampleProvider);

                // 5. Запись результата
                WaveFileWriter.CreateWaveFile(outputPath, waveProvider);
            });
        }

        /// <summary>
        /// Разбивает аудиофайл на чанки заданной длительности (в секундах)
        /// Возвращает список путей к временным файлам чанков
        /// </summary>
        public static async Task<List<string>> SplitAudioIntoChunksAsync(
            string inputPath,
            int chunkDurationSeconds = 300, // 5 минут по умолчанию
            string? outputDirectory = null)
        {
            var chunkPaths = new List<string>();

            await Task.Run(() =>
            {
                using var reader = new AudioFileReader(inputPath);

                // Приводим к формату Whisper (16kHz Mono)
                ISampleProvider sampleProvider = reader;

                if (reader.WaveFormat.Channels > 1)
                {
                    var monoProvider = new StereoToMonoSampleProvider(sampleProvider)
                    {
                        LeftVolume = 0.5f,
                        RightVolume = 0.5f
                    };
                    sampleProvider = monoProvider;
                }

                if (reader.WaveFormat.SampleRate != 16000)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
                }

                var waveProvider = new SampleToWaveProvider16(sampleProvider);

                // Рассчитываем количество чанков
                var totalSamples = reader.Length / 4; // 4 байта на сэмпл (float)
                var samplesPerSecond = 16000;
                var samplesPerChunk = samplesPerSecond * chunkDurationSeconds;
                var totalChunks = (int)Math.Ceiling((double)totalSamples / samplesPerChunk);

                outputDirectory ??= Path.GetTempPath();

                for (int i = 0; i < totalChunks; i++)
                {
                    var chunkPath = Path.Combine(outputDirectory, $"chunk_{Guid.NewGuid()}.wav");
                    chunkPaths.Add(chunkPath);

                    var startSample = i * samplesPerChunk;
                    var samplesToRead = Math.Min(samplesPerChunk, totalSamples - startSample);

                    // Читаем чанк и записываем в отдельный файл
                    using var writer = new WaveFileWriter(chunkPath, new WaveFormat(16000, 16, 1));

                    var buffer = new float[samplesToRead];
                    var readSamples = sampleProvider.Read(buffer, 0, (int)samplesToRead);

                    // Конвертируем float в short для записи
                    var shortBuffer = new short[readSamples];
                    for (int j = 0; j < readSamples; j++)
                    {
                        shortBuffer[j] = (short)(buffer[j] * short.MaxValue);
                    }

                    // Конвертируем short[] в byte[] для записи
                    var byteBuffer = new byte[readSamples * 2];
                    Buffer.BlockCopy(shortBuffer, 0, byteBuffer, 0, readSamples * 2);

                    writer.Write(byteBuffer, 0, readSamples * 2);
                }
            });

            return chunkPaths;
        }


        /// <summary>
        /// Получает длительность аудиофайла в секундах
        /// </summary>
        public static TimeSpan GetAudioDuration(string filePath)
        {
            using var reader = new AudioFileReader(filePath);
            return reader.TotalTime;
        }
    }
}
