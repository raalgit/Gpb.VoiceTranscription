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
using Whisper.net.Ggml;

namespace Gpb.VoiceTranscription.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly string _modelsDirectory;
        private WhisperTranscriptionService? _transcriptionService;
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
        [ObservableProperty] private List<WhisperModelItem> _availableModels = new();
        [ObservableProperty] private WhisperModelItem? _selectedModel;
        [ObservableProperty] private bool _isDownloadingModel;

        public bool HasResult => !string.IsNullOrEmpty(TranscriptionResult);

        public MainViewModel()
        {
            _modelsDirectory = Path.Combine(Environment.CurrentDirectory, "Assets", "Models");
            Directory.CreateDirectory(_modelsDirectory);
            
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

            // Инициализация списка моделей
            InitializeModelsList();
            
            _ = InitializeAsync();
        }

        /// <summary>
        /// Инициализация списка доступных моделей Whisper
        /// </summary>
        private void InitializeModelsList()
        {
            var models = new List<WhisperModelItem>
            {
                new WhisperModelItem { Name = "Tiny", Type = GgmlType.Tiny, FileName = "ggml-tiny.bin", Description = "Самая быстрая, низкое качество" },
                new WhisperModelItem { Name = "Base", Type = GgmlType.Base, FileName = "ggml-base.bin", Description = "Быстрая, среднее качество" },
                new WhisperModelItem { Name = "Small", Type = GgmlType.Small, FileName = "ggml-small.bin", Description = "Баланс скорости и качества" },
                new WhisperModelItem { Name = "Medium", Type = GgmlType.Medium, FileName = "ggml-medium.bin", Description = "Высокое качество, медленнее" },
                new WhisperModelItem { Name = "Large-v1", Type = GgmlType.LargeV1, FileName = "ggml-large-v1.bin", Description = "Очень высокое качество" },
                new WhisperModelItem { Name = "Large-v2", Type = GgmlType.LargeV2, FileName = "ggml-large-v2.bin", Description = "Очень высокое качество" },
                new WhisperModelItem { Name = "Large-v3", Type = GgmlType.LargeV3, FileName = "ggml-large-v3.bin", Description = "Лучшее качество" }
            };

            // Проверяем, какие модели уже скачаны
            foreach (var model in models)
            {
                var modelPath = Path.Combine(_modelsDirectory, model.FileName);
                model.IsDownloaded = File.Exists(modelPath);
            }

            AvailableModels = models;
            
            // Выбираем первую скачанную модель или первую в списке
            SelectedModel = AvailableModels.FirstOrDefault(m => m.IsDownloaded) ?? AvailableModels.First();
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
            await LoadSelectedModelAsync();
        }

        /// <summary>
        /// Загрузка выбранной модели
        /// </summary>
        private async Task LoadSelectedModelAsync()
        {
            if (SelectedModel == null)
                return;

            var modelPath = Path.Combine(_modelsDirectory, SelectedModel.FileName);
            
            // Если модель не скачана, скачиваем её
            if (!SelectedModel.IsDownloaded)
            {
                await DownloadModelAsync(SelectedModel);
            }

            // Создаём сервис транскрибации
            _transcriptionService?.Dispose();
            _transcriptionService = new WhisperTranscriptionService(modelPath);

            StatusMessage = "⏳ Загрузка модели...";

            // Подписываемся на события прогресса
            _transcriptionService.ProgressChanged += msg =>
                Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
            _transcriptionService.ProgressPercentChanged += progress =>
                Application.Current.Dispatcher.Invoke(() => ProgressValue = progress);

            var success = await _transcriptionService.InitializeAsync();
            StatusMessage = success ? "✅ Готов к работе" : "❌ Ошибка загрузки модели";
        }

        /// <summary>
        /// Скачивание модели
        /// </summary>
        private async Task DownloadModelAsync(WhisperModelItem model)
        {
            IsDownloadingModel = true;
            IsProcessing = true;
            StatusMessage = $"📥 Скачивание модели {model.Name}...";

            try
            {
                var modelPath = Path.Combine(_modelsDirectory, model.FileName);
                
                await WhisperTranscriptionService.DownloadModelAsync(modelPath, model.Type);
                
                model.IsDownloaded = true;
                StatusMessage = $"✅ Модель {model.Name} скачана";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Ошибка скачивания модели: {ex.Message}";
                MessageBox.Show($"Не удалось скачать модель {model.Name}: {ex.Message}", 
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsDownloadingModel = false;
                IsProcessing = false;
            }
        }

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

        [RelayCommand(CanExecute = nameof(IsModelReady))]
        private async Task StartTranscriptionAsync()
        {
            if (string.IsNullOrEmpty(SelectedFilePath) || _transcriptionService == null || SelectedModel == null) 
                return;

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
                    modelName: SelectedModel.Name,
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

        /// <summary>
        /// Проверка готовности модели к транскрибации
        /// </summary>
        private bool IsModelReady => SelectedModel?.IsDownloaded == true && !IsDownloadingModel && _transcriptionService != null;

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

        /// <summary>
        /// Обработка изменения свойства SelectedModel
        /// </summary>
        partial void OnSelectedModelChanged(WhisperModelItem? value)
        {
            if (value != null && !IsDownloadingModel)
            {
                _ = ModelSelectionChangedAsync();
            }
        }

        /// <summary>
        /// Обработка выбора модели из ComboBox
        /// </summary>
        [RelayCommand]
        private async Task ModelSelectionChangedAsync()
        {
            if (SelectedModel == null)
                return;

            // Если модель не скачана, запускаем скачивание
            if (!SelectedModel.IsDownloaded)
            {
                await DownloadModelAsync(SelectedModel);
            }

            // После скачивания (или если модель уже была скачана) загружаем её
            if (SelectedModel.IsDownloaded)
            {
                await LoadSelectedModelAsync();
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
