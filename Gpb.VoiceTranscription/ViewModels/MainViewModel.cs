using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gpb.VoiceTranscription.Models;
using Gpb.VoiceTranscription.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Gpb.VoiceTranscription.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly WhisperTranscriptionService _transcriptionService;
        private readonly AudioRecordingService _recordingService;
        private CancellationTokenSource? _cts;

        [ObservableProperty] private string _statusMessage = "🟡 Ожидание...";
        [ObservableProperty] private double _progressValue;
        [ObservableProperty] private bool _isProcessing;
        [ObservableProperty] private string _selectedFilePath = string.Empty;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(HasResult))] private string _transcriptionResult = string.Empty;
        [ObservableProperty] private bool _autoConvertAudio = true; // ✅ По умолчанию включено
        [ObservableProperty] private bool _isRecording;
        [ObservableProperty] private int _selectedDeviceIndex;
        [ObservableProperty] private string? _selectedLoopbackDeviceId;
        [ObservableProperty] private List<AudioDeviceItem> _availableDevices = new();
        [ObservableProperty] private bool _useChunkingForLargeFiles = true; // ✅ По умолчанию включена чанковая обработка

        public bool HasResult => !string.IsNullOrEmpty(TranscriptionResult);

        public MainViewModel(string modelPath)
        {
            _transcriptionService = new WhisperTranscriptionService(modelPath);
            _recordingService = new AudioRecordingService();

            // Подписка на события сервиса записи
            _recordingService.RecordingStateChanged += isRecording => 
                Application.Current.Dispatcher.Invoke(() => IsRecording = isRecording);
            _recordingService.ErrorOccurred += msg => 
                Application.Current.Dispatcher.Invoke(() => StatusMessage = $"❌ {msg}");

            // Загрузка списка устройств с ID
            AvailableDevices = GetAvailableDevicesWithIds();
            if (AvailableDevices.Count > 0)
            {
                SelectedDeviceIndex = AvailableDevices[0].Index;
                SelectedLoopbackDeviceId = AvailableDevices[0].DeviceId;
            }

            _ = InitializeAsync();
        }

        /// <summary>
        /// Получить список устройств с их ID для Loopback
        /// </summary>
        private static List<AudioDeviceItem> GetAvailableDevicesWithIds()
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

        private async Task InitializeAsync()
        {
            StatusMessage = "⏳ Загрузка модели...";

            // Подписываемся на события прогресса
            _transcriptionService.ProgressChanged += msg =>
                Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
            _transcriptionService.ProgressPercentChanged += progress =>
                Application.Current.Dispatcher.Invoke(() => ProgressValue = progress);

            var success = await _transcriptionService.InitializeAsync();
            StatusMessage = success ? "✅ Готов к работе" : "❌ Ошибка загрузки модели";
        }

        // ✅ Автоматически генерирует RelayCommand
        [RelayCommand]
        private void SelectFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            { 
                Filter = "Аудио файлы (*.wav;*.mp3;*.mp4;*.m4a)|*.wav;*.mp3;*.mp4;*.m4a|WAV файлы (*.wav)|*.wav|MP3 файлы (*.mp3)|*.mp3|MP4 файлы (*.mp4)|*.mp4|M4A файлы (*.m4a)|*.m4a|Все файлы|*.*" 
            };
            if (dialog.ShowDialog() == true)
                SelectedFilePath = dialog.FileName;
        }

        // ✅ Автоматически генерирует AsyncRelayCommand с блокировкой параллельного запуска
        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task StartTranscriptionAsync()
        {
            if (string.IsNullOrEmpty(SelectedFilePath)) return;

            // Проверка формата файла
            if (!AudioConverter.IsSupportedAudioFormat(SelectedFilePath))
            {
                StatusMessage = "❌ Неподдерживаемый формат файла";
                MessageBox.Show("Пожалуйста, выберите файл в формате WAV, MP3, MP4 или M4A", "Ошибка", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsProcessing = true;
            _cts = new CancellationTokenSource();
            TranscriptionResult = string.Empty;
            ProgressValue = 0;

            string processPath = SelectedFilePath;
            string? tempFilePath = null;

            try
            {
                // 🔄 Логика конвертации
                if (AutoConvertAudio)
                {
                    StatusMessage = "🔄 Проверка формата аудио...";
                    tempFilePath = Path.Combine(Path.GetTempPath(), $"whisper_temp_{Guid.NewGuid()}.wav");

                    if (!AudioConverter.IsWhisperCompatible(SelectedFilePath))
                    {
                        StatusMessage = "⚙️ Конвертация в 16kHz Mono WAV...";
                        await AudioConverter.ConvertToWhisperFormatAsync(SelectedFilePath, tempFilePath);
                        processPath = tempFilePath;
                    }
                    else
                    {
                        // Файл уже подходит, временный не нужен
                        if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                        tempFilePath = null;
                    }
                }

                StatusMessage = "🎙️ Начинаю транскрибацию...";

                var transcription = await _transcriptionService.TranscribeAsync(
                    processPath,
                    modelName: "small",
                    useChunking: UseChunkingForLargeFiles,
                    _cts.Token);

                TranscriptionResult = transcription.ToMarkdown();
                StatusMessage = $"✅ Готово! {transcription.Segments.Count} сегментов, " +
                               $"{transcription.ProcessingTimeMs / 1000:F1} сек.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "⚠️ Транскрибация отменена";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Ошибка: {ex.Message}";
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 🧹 Очищаем временный файл
                if (tempFilePath != null && File.Exists(tempFilePath))
                    File.Delete(tempFilePath);

                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        [RelayCommand(CanExecute = nameof(IsProcessing))]
        private void CancelTranscription() => _cts?.Cancel();

        [RelayCommand()]
        private void SaveResult()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            { Filter = "Markdown (*.md)|*.md", FileName = "transcript.md" };
            if (dialog.ShowDialog() == true)
                File.WriteAllText(dialog.FileName, TranscriptionResult);
        }

        // ✅ Команды для записи аудио
        [RelayCommand]
        private async Task StartRecordingAsync()
        {
            if (_recordingService.IsRecording)
                return;

            IsProcessing = true;
            StatusMessage = "🔴 Запись...";
            TranscriptionResult = string.Empty;

            try
            {
                // Находим выбранное устройство в списке
                var selectedDevice = AvailableDevices.FirstOrDefault(d => d.Index == SelectedDeviceIndex);

                // Если выбрано устройство Loopback, используем useLoopback=true
                bool useLoopback = selectedDevice?.IsLoopback == true;
                int deviceIndex = useLoopback ? 0 : SelectedDeviceIndex;

                await _recordingService.StartRecordingAsync(deviceIndex, useLoopback, SelectedLoopbackDeviceId);
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Ошибка записи: {ex.Message}";
                IsProcessing = false;
            }
        }

        [RelayCommand]
        private async Task StopRecordingAsync()
        {
            if (!_recordingService.IsRecording)
                return;

            try
            {
                var recordedFilePath = await _recordingService.StopRecordingAsync();

                if (!string.IsNullOrEmpty(recordedFilePath))
                {
                    SelectedFilePath = recordedFilePath;
                    StatusMessage = "✅ Запись завершена. Файл готов к транскрибации.";
                    
                    // Автоматически запускаем транскрибацию после записи
                    await StartTranscriptionAsync();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Ошибка остановки записи: {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void Dispose()
        {
            _transcriptionService?.Dispose();
            _recordingService?.Dispose();
            _cts?.Dispose();
        }
    }
}
