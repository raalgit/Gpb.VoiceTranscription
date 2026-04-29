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
    /// Сервис для записи аудио с микрофона, системного звука (WasapiLoopback) или обоих источников одновременно
    /// </summary>
    public class AudioRecordingService : IDisposable
    {
        #region Fields

        private IWaveIn? _waveIn;
        private WasapiLoopbackCapture? _loopbackCapture;
        private WaveFileWriter? _writer;
        private WaveFileWriter? _loopbackWriter;
        private string? _tempFilePath;
        private string? _loopbackTempFilePath;
        private bool _isRecording;
        private bool _isCombinedRecording; // Запись обоих источников
        private readonly object _lock = new();

        #endregion

        #region Properties

        public bool IsRecording => _isRecording;
        public bool IsCombinedRecording => _isCombinedRecording;

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
                        DeviceId = null,
                        IsCombined = false
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
                        DeviceId = device.ID,
                        IsCombined = false
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
                    DeviceId = null,
                    IsCombined = false
                });
            }

            // Добавляем опцию комбинированной записи (микрофон по умолчанию + loopback по умолчанию)
            devices.Add(new AudioDeviceItem
            {
                Index = -200,
                Name = "🎧 Микрофон + Наушники (одновременно)",
                IsLoopback = false,
                DeviceId = null,
                IsCombined = true
            });

            return devices;
        }

        /// <summary>
        /// Начать запись с указанного устройства
        /// </summary>
        /// <param name="deviceIndex">Индекс устройства (-1 для устройства по умолчанию)</param>
        /// <param name="useLoopback">Если true, записывать системный звук (наушники/динамики) через WASAPI Loopback</param>
        /// <param name="loopbackDeviceId">ID устройства воспроизведения для Loopback (null для устройства по умолчанию)</param>
        /// <param name="isCombined">Если true, записывать одновременно микрофон и наушники</param>
        public async Task StartRecordingAsync(int deviceIndex = -1, bool useLoopback = false, string? loopbackDeviceId = null, bool isCombined = false)
        {
            if (_isRecording)
                throw new InvalidOperationException("Запись уже идёт");

            lock (_lock)
            {
                try
                {
                    _tempFilePath = Path.Combine(Path.GetTempPath(), $"recording_{Guid.NewGuid()}.wav");
                    
                    var waveFormat = new WaveFormat(16000, 16, 1); // 16kHz Mono для Whisper

                    if (isCombined)
                    {
                        // Комбинированная запись: микрофон + loopback
                        _isCombinedRecording = true;
                        
                        // Запись с микрофона (устройство по умолчанию)
                        _waveIn = new WaveInEvent
                        {
                            DeviceNumber = 0,
                            WaveFormat = waveFormat
                        };
                        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);
                        _waveIn.DataAvailable += OnDataAvailable;
                        _waveIn.RecordingStopped += OnRecordingStopped;

                        // Запись системного звука (loopback по умолчанию)
                        _loopbackTempFilePath = Path.Combine(Path.GetTempPath(), $"loopback_{Guid.NewGuid()}.wav");
                        var loopbackDevice = GetLoopbackDevice(null);
                        _loopbackCapture = new WasapiLoopbackCapture(loopbackDevice)
                        {
                            WaveFormat = waveFormat
                        };
                        _loopbackWriter = new WaveFileWriter(_loopbackTempFilePath, _loopbackCapture.WaveFormat);
                        _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
                        _loopbackCapture.RecordingStopped += OnLoopbackRecordingStopped;

                        // Запускаем оба источника
                        _waveIn.StartRecording();
                        _loopbackCapture.StartRecording();
                    }
                    else if (useLoopback)
                    {
                        // Запись системного звука через WASAPI Loopback
                        var captureDevice = GetLoopbackDevice(loopbackDeviceId);
                        _waveIn = new WasapiLoopbackCapture(captureDevice)
                        {
                            WaveFormat = waveFormat
                        };
                        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);
                        _waveIn.DataAvailable += OnDataAvailable;
                        _waveIn.RecordingStopped += OnRecordingStopped;
                        _waveIn.StartRecording();
                    }
                    else
                    {
                        // Запись с микрофона
                        _waveIn = new WaveInEvent
                        {
                            DeviceNumber = deviceIndex >= 0 ? deviceIndex : 0,
                            WaveFormat = waveFormat
                        };
                        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);
                        _waveIn.DataAvailable += OnDataAvailable;
                        _waveIn.RecordingStopped += OnRecordingStopped;
                        _waveIn.StartRecording();
                    }

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
                    
                    if (_isCombinedRecording && _loopbackCapture != null)
                    {
                        _loopbackCapture.StopRecording();
                    }
                    
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

            string? resultPath = _tempFilePath;
            
            if (_isCombinedRecording)
            {
                // Объединяем два аудиофайла в один
                _loopbackWriter?.Dispose();
                _loopbackWriter = null;
                
                resultPath = CombineAudioFiles(_tempFilePath, _loopbackTempFilePath);
                
                if (!string.IsNullOrEmpty(_loopbackTempFilePath) && File.Exists(_loopbackTempFilePath))
                {
                    File.Delete(_loopbackTempFilePath);
                }
                _loopbackTempFilePath = null;
                
                _isCombinedRecording = false;
            }
            
            _tempFilePath = null;

            RecordingStateChanged?.Invoke(false);

            return resultPath;
        }

        /// <summary>
        /// Объединить два WAV файла микшированием
        /// </summary>
        private string CombineAudioFiles(string? micFilePath, string? loopbackFilePath)
        {
            if (string.IsNullOrEmpty(micFilePath) || string.IsNullOrEmpty(loopbackFilePath))
                return micFilePath ?? loopbackFilePath;

            var outputPath = Path.Combine(Path.GetTempPath(), $"combined_{Guid.NewGuid()}.wav");
            
            using var micReader = new WaveFileReader(micFilePath);
            using var loopbackReader = new WaveFileReader(loopbackFilePath);
            
            // Используем формат первого файла
            var waveFormat = micReader.WaveFormat;
            
            using var writer = new WaveFileWriter(outputPath, waveFormat);
            
            var maxLength = Math.Max(micReader.Length, loopbackReader.Length);
            var buffer1 = new byte[4096];
            var buffer2 = new byte[4096];
            
            int bytesRead1, bytesRead2;
            
            while ((bytesRead1 = micReader.Read(buffer1, 0, buffer1.Length)) > 0 || 
                   (bytesRead2 = loopbackReader.Read(buffer2, 0, buffer2.Length)) > 0)
            {
                // Микшируем семплы (простое сложение с ограничением)
                int samplesToWrite = Math.Max(bytesRead1, bytesRead2) / 2; // 16-bit = 2 bytes per sample
                
                for (int i = 0; i < samplesToWrite; i++)
                {
                    short sample1 = bytesRead1 > i * 2 ? BitConverter.ToInt16(buffer1, i * 2) : (short)0;
                    short sample2 = bytesRead2 > i * 2 ? BitConverter.ToInt16(buffer2, i * 2) : (short)0;
                    
                    // Микширование с уменьшением громкости для предотвращения клиппинга
                    int mixed = (sample1 + sample2) / 2;
                    short clamped = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mixed));
                    
                    writer.Write(BitConverter.GetBytes(clamped));
                }
            }
            
            return outputPath;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_writer != null && e.Buffer != null)
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }

        private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_loopbackWriter != null && e.Buffer != null)
            {
                _loopbackWriter.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            // Обработка окончания записи
        }
        
        private void OnLoopbackRecordingStopped(object? sender, StoppedEventArgs e)
        {
            // Обработка окончания записи loopback
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

                if (_loopbackCapture != null)
                {
                    _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
                    _loopbackCapture.RecordingStopped -= OnLoopbackRecordingStopped;
                    _loopbackCapture.Dispose();
                    _loopbackCapture = null;
                }

                if (_writer != null)
                {
                    _writer.Dispose();
                    _writer = null;
                }

                if (_loopbackWriter != null)
                {
                    _loopbackWriter.Dispose();
                    _loopbackWriter = null;
                }

                if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
                {
                    File.Delete(_tempFilePath);
                    _tempFilePath = null;
                }

                if (!string.IsNullOrEmpty(_loopbackTempFilePath) && File.Exists(_loopbackTempFilePath))
                {
                    File.Delete(_loopbackTempFilePath);
                    _loopbackTempFilePath = null;
                }

                _isRecording = false;
                _isCombinedRecording = false;
            }
        }

        #endregion
    }
}
