using Gpb.VoiceTranscription.Models;
using NAudio.CoreAudioApi;
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
        #region Fields

        private IWaveIn? _waveIn;
        private WaveFileWriter? _writer;
        private string? _tempFilePath;
        private bool _isRecording;
        private readonly object _lock = new();

        #endregion

        #region Properties

        public bool IsRecording => _isRecording;

        #endregion

        #region Events

        public event Action<bool>? RecordingStateChanged;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region Methods

        /// <summary>
        /// Получить список устройств с их ID для Loopback
        /// </summary>
        public List<AudioDeviceItem> GetAvailableDevicesWithIds()
        {
            var devices = new List<AudioDeviceItem>();

            // Микрофоны (WaveIn devices)
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    devices.Add(new AudioDeviceItem
                    {
                        Index = i,
                        Name = $"🎤 {caps.ProductName}",
                        IsLoopback = false,
                        DeviceId = null
                    });
                }
                catch
                {
                    // Пропускаем недоступные устройства
                }
            }

            // Добавляем опции для записи системного звука (Loopback) - каждое устройство воспроизведения
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in renderDevices)
                {
                    devices.Add(new AudioDeviceItem
                    {
                        Index = -100,
                        Name = $"🔊 Loopback: {device.FriendlyName}",
                        IsLoopback = true,
                        DeviceId = device.ID
                    });
                }
            }
            catch
            {
                // Если не удалось получить устройства, добавляем общую опцию
                devices.Add(new AudioDeviceItem
                {
                    Index = -100,
                    Name = "🔊 Системный звук (Loopback)",
                    IsLoopback = true,
                    DeviceId = null
                });
            }

            return devices;
        }

        /// <summary>
        /// Начать запись с указанного устройства
        /// </summary>
        /// <param name="deviceIndex">Индекс устройства (-1 для устройства по умолчанию)</param>
        /// <param name="useLoopback">Если true, записывать системный звук (наушники/динамики) через WASAPI Loopback</param>
        /// <param name="loopbackDeviceId">ID устройства воспроизведения для Loopback (null для устройства по умолчанию)</param>
        public async Task StartRecordingAsync(int deviceIndex = -1, bool useLoopback = false, string? loopbackDeviceId = null)
        {
            if (_isRecording)
                throw new InvalidOperationException("Запись уже идёт");

            lock (_lock)
            {
                try
                {
                    _tempFilePath = Path.Combine(Path.GetTempPath(), $"recording_{Guid.NewGuid()}.wav");

                    var waveFormat = new WaveFormat(16000, 16, 1); // 16kHz Mono для Whisper

                    if (useLoopback)
                    {
                        // Запись системного звука через WASAPI Loopback
                        var captureDevice = GetLoopbackDevice(loopbackDeviceId);
                        _waveIn = new WasapiLoopbackCapture(captureDevice)
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
        }

        /// <summary>
        /// Получить устройство Loopback для записи системного звука
        /// </summary>
        private MMDevice GetLoopbackDevice(string? deviceId = null)
        {
            using var enumerator = new MMDeviceEnumerator();
            
            if (!string.IsNullOrEmpty(deviceId))
            {
                // Попытка найти устройство по ID
                try
                {
                    return enumerator.GetDevice(deviceId);
                }
                catch
                {
                    // Если не найдено, используем устройство по умолчанию
                }
            }
            
            // Устройство воспроизведения по умолчанию
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        /// <summary>
        /// Остановить запись
        /// </summary>
        public async Task<string?> StopRecordingAsync()
        {
            if (!_isRecording || _waveIn == null)
                return null;

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

        #endregion

        #region Dispose

        public void Dispose()
        {
            Cleanup();
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

        #endregion
    }
}
