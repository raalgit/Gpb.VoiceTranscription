using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Threading.Tasks;

namespace Gpb.VoiceTranscription.Services
{
    public static class AudioConverter
    {
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

        public static async Task ConvertToWhisperFormatAsync(string inputPath, string outputPath)
        {
            await Task.Run(() =>
            {
                // 1. Декодируем входной файл в IEEE Float (32-bit)
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
    }
}
