using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Gpb.VoiceTranscription.Services
{
    /// <summary>
    /// Сервис для записи аудио с микрофона или системного звука (WasapiLoopback)
    /// </summary>
    public class AudioRecordingService : IDisposable
    {
        private IWaveIn? _waveIn;
        private WaveFileWriter? _writer;
        private string? _tempFilePath;
        private bool _isRecording;
        private readonly object _lock = new();

        public bool IsRecording => _isRecording;

        public event Action<bool>? RecordingStateChanged;
        public event Action<string>? ErrorOccurred;

        /// <summary>
        /// Начать запись с указанного устройства
        /// </summary>
        /// <param name="deviceIndex">Индекс устройства (-1 для устройства по умолчанию)</param>
        /// <param name="useLoopback">Если true, записывать системный звук (наушники/динамики) через WASAPI Loopback</param>
        public async Task StartRecordingAsync(int deviceIndex = -1, bool useLoopback = false)
        {
            if (_isRecording)
                throw new InvalidOperationException("Запись уже идёт");

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        _tempFilePath = Path.Combine(Path.GetTempPath(), $"recording_{Guid.NewGuid()}.wav");

                        var waveFormat = new WaveFormat(16000, 16, 1); // 16kHz Mono для Whisper

                        if (useLoopback)
                        {
                            // Запись системного звука через WASAPI Loopback
                            _waveIn = new WasapiCapture(useLoopback: true)
                            {
                                WaveFormat = waveFormat
                            };
                        }
                        else
                        {
                            // Запись с микрофона
                            _waveIn = new WaveInEvent
                            {
                                DeviceNumber = deviceIndex >= 0 ? deviceIndex : 0,
                                WaveFormat = waveFormat
                            };
                        }

                        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);

                        _waveIn.DataAvailable += OnDataAvailable;
                        _waveIn.RecordingStopped += OnRecordingStopped;

                        _waveIn.StartRecording();
                        _isRecording = true;
                        RecordingStateChanged?.Invoke(true);
                    }
                    catch (Exception ex)
                    {
                        Cleanup();
                        ErrorOccurred?.Invoke($"Ошибка начала записи: {ex.Message}");
                        throw;
                    }
                }
            });
        }

        /// <summary>
        /// Остановить запись
        /// </summary>
        public async Task<string?> StopRecordingAsync()
        {
            if (!_isRecording || _waveIn == null)
                return null;

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        _waveIn.StopRecording();
                        _isRecording = false;
                    }
                    catch (Exception ex)
                    {
                        ErrorOccurred?.Invoke($"Ошибка остановки записи: {ex.Message}");
                        throw;
                    }
                }
            });

            // Ждём завершения обработки последних данных
            await Task.Delay(500);

            _writer?.Dispose();
            _writer = null;

            var resultPath = _tempFilePath;
            _tempFilePath = null;

            RecordingStateChanged?.Invoke(false);

            return resultPath;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_writer != null && e.Buffer != null)
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            // Обработка окончания записи
        }

        /// <summary>
        /// Получить список доступных устройств записи
        /// </summary>
        public static (int index, string name, bool isLoopback)[] GetAvailableDevices()
        {
            var devices = new List<(int, string, bool)>();

            // Микрофоны (WaveIn devices)
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    devices.Add((i, $"🎤 {caps.ProductName}", false));
                }
                catch
                {
                    // Пропускаем недоступные устройства
                }
            }

            // Добавляем опцию для записи системного звука (Loopback)
            devices.Add((-100, "🔊 Системный звук (Loopback)", true));

            return devices.ToArray();
        }

        private void Cleanup()
        {
            lock (_lock)
            {
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnDataAvailable;
                    _waveIn.RecordingStopped -= OnRecordingStopped;
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                if (_writer != null)
                {
                    _writer.Dispose();
                    _writer = null;
                }

                if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
                {
                    File.Delete(_tempFilePath);
                    _tempFilePath = null;
                }

                _isRecording = false;
            }
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}
