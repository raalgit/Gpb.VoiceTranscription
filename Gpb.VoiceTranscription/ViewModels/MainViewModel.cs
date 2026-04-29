using CommunityToolkit.Mvvm.ComponentModel;
using Gpb.VoiceTranscription.Models;
using Gpb.VoiceTranscription.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Whisper.net.Ggml;

namespace Gpb.VoiceTranscription.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        #region Fields

        private WhisperTranscriptionService? _transcriptionService;
        private readonly AudioRecordingService _recordingService;
        private CancellationTokenSource? _cts;
        private Models.TranscriptionResult? _currentTranscription; // ✅ Поле для хранения результата текущей транскрипции

        #endregion

        #region Properties 

        /// <summary>
        /// Проверка готовности модели к транскрибации
        /// </summary>
        private bool IsModelReady => SelectedModel?.IsDownloaded == true && !IsDownloadingModel && _transcriptionService != null;
        
        public bool HasResult => !string.IsNullOrEmpty(TranscriptionResult);

        #endregion

        #region Observable Properties

        [ObservableProperty] private bool _isProcessing;
        [ObservableProperty] private bool _isRecording;
        [ObservableProperty] private bool _canTranscribe;
        [ObservableProperty] private bool _autoConvertAudio = true; // ✅ По умолчанию включено
        [ObservableProperty] private bool _useChunkingForLargeFiles = true; // ✅ По умолчанию включена чанковая обработка
        [ObservableProperty] private bool _enableSummarization = AppSettings.EnableSummarization; // ✅ Суммаризация по умолчанию из конфига
        [ObservableProperty] private bool _isDownloadingModel;
        [ObservableProperty] private bool _isSummarizing;
        [ObservableProperty] private string _statusMessage = "🟡 Ожидание...";
        [ObservableProperty] private string _selectedFilePath = string.Empty;
        [ObservableProperty] private string _transcriptionResult = string.Empty;
        [ObservableProperty] private string? _summaryResult = string.Empty;
        [ObservableProperty] private string? _selectedLoopbackDeviceId;
        [ObservableProperty] private double _progressValue;
        [ObservableProperty] private int _downloadProgress = 0;
        [ObservableProperty] private int _selectedDeviceIndex;
        [ObservableProperty] private List<AudioDeviceItem> _availableDevices = [];
        [ObservableProperty] private List<WhisperModelItem> _availableModels = [];
        [ObservableProperty] private WhisperModelItem? _selectedModel;

        private readonly ISummarizationService? _summarizationService;

        #endregion

        #region Ctor

        public MainViewModel()
        {   
            _recordingService = new AudioRecordingService();
            
            // Инициализация сервиса суммаризации (если настроен API ключ)
            if (!string.IsNullOrEmpty(AppSettings.SummarizationApiKey))
            {
                _summarizationService = new SummarizationService();
            }

            // Подписка на события сервиса записи
            _recordingService.RecordingStateChanged += isRecording => 
                Application.Current.Dispatcher.Invoke(() => IsRecording = isRecording);
            _recordingService.ErrorOccurred += msg => 
                Application.Current.Dispatcher.Invoke(() => StatusMessage = $"❌ {msg}");

            // Загрузка списка устройств с ID
            AvailableDevices = _recordingService.GetAvailableDevicesWithIds();
            if (AvailableDevices.Count > 0)
            {
                SelectedDeviceIndex = AvailableDevices[0].Index;
                SelectedLoopbackDeviceId = AvailableDevices[0].DeviceId;
            }

            _ = InitializeAsync();
        }

        #endregion

        #region Methods

        private async Task InitializeAsync()
        {
            await InitializeModelsAsync();
            await LoadSelectedModelAsync();
        }

        private async Task InitializeModelsAsync()
        {
            var modelsList = ModelStorageService.GetAllModels();

            // Обновляем коллекцию в UI потоке
            Application.Current.Dispatcher.Invoke(() =>
            {
                AvailableModels = modelsList;

                // Опционально: выбираем первую скачанную модель по умолчанию
                var firstDownloaded = AvailableModels.FirstOrDefault(m => m.IsDownloaded);
                if (firstDownloaded != null)
                {
                    SelectedModel = firstDownloaded;
                }
                else if (AvailableModels.Count > 0)
                {
                    // Если ни одна не скачана, выбираем первую в списке (она будет disabled, но визуально выделена)
                    SelectedModel = AvailableModels[0];
                }
            });
        }

        /// <summary>
        /// Загрузка выбранной модели
        /// </summary>
        private async Task LoadSelectedModelAsync()
        {
            if (SelectedModel == null)
                return;

            var modelPath = ModelStorageService.GetModelPath(SelectedModel.Type);
            
            // Если модель не скачана, скачиваем её
            if (!SelectedModel.IsDownloaded)
            {
                await DownloadModelAsync(SelectedModel.Type);
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

        private async Task DownloadModelAsync(GgmlType modelType)
        {
            var fileName = $"ggml-{modelType.ToString().ToLower()}.bin";
            var folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperTranscriber", "Models");
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            var filePath = Path.Combine(folderPath, fileName);

            // Если файл уже есть, ничего не делаем
            if (File.Exists(filePath))
            {
                UpdateModelStatus(modelType, true);
                return;
            }

            try
            {
                IsDownloadingModel = true;
                DownloadProgress = 0;

                // Читаем настройки
                var baseUrl = AppSettings.WhisperBaseUrl.TrimEnd('/');
                var modelUrl = $"{baseUrl}/{fileName}";

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(30);

                // Скачиваем файл явно в временный файл, чтобы избежать проблем с потоками в WhisperGgmlDownloader
                var tempFilePath = filePath + ".tmp";

                using (var response = await httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int read;

                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, read));
                            totalRead += read;

                            if (totalBytes > 0)
                            {
                                var progress = (int)(totalRead * 100 / totalBytes);
                                Application.Current.Dispatcher.Invoke(() => DownloadProgress = progress);
                            }
                        }
                    }
                }

                // Перемещаем файл на место только после успешной загрузки
                File.Move(tempFilePath, filePath, overwrite: true);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    DownloadProgress = 100;
                    IsDownloadingModel = false;
                    UpdateModelStatus(modelType, true);
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsDownloadingModel = false;
                    MessageBox.Show($"Ошибка скачивания модели: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                });

                // Удаляем битый файл, если он остался
                if (File.Exists(filePath + ".tmp"))
                    File.Delete(filePath + ".tmp");
            }
        }

        private void UpdateModelStatus(GgmlType modelType, bool isDownloaded)
        {
            var model = AvailableModels.FirstOrDefault(m => m.Type == modelType);
            if (model != null)
            {
                model.IsDownloaded = isDownloaded;

                // Если это была выбранная модель, проверяем возможность транскрибации
                if (SelectedModel?.Type == modelType)
                {
                    OnPropertyChanged(nameof(CanTranscribe));
                    OnPropertyChanged(nameof(IsModelReady));
                }
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

        #endregion

        #region Dispose

        public void Dispose()
        {
            _transcriptionService?.Dispose();
            (_summarizationService as IDisposable)?.Dispose();
            _recordingService?.Dispose();
            _cts?.Dispose();
        }

        #endregion
    }
}
