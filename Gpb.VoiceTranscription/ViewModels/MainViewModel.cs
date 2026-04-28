using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gpb.VoiceTranscription.Services;
using System;
using System.IO;
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
        [ObservableProperty] private string _transcriptionResult = string.Empty;
        [ObservableProperty] private bool _autoConvertAudio = true; // ✅ По умолчанию включено
        [ObservableProperty] private bool _isRecording;
        [ObservableProperty] private int _selectedDeviceIndex;
        [ObservableProperty] private (int index, string name, bool isLoopback)[] _availableDevices = Array.Empty<(int, string, bool)>();

        private bool HasResult => !string.IsNullOrEmpty(TranscriptionResult);


        public MainViewModel(string modelPath)
        {
            _transcriptionService = new WhisperTranscriptionService(modelPath);
            _recordingService = new AudioRecordingService();

            // Подписка на события сервиса записи
            _recordingService.RecordingStateChanged += isRecording => 
                Application.Current.Dispatcher.Invoke(() => IsRecording = isRecording);
            _recordingService.ErrorOccurred += msg => 
                Application.Current.Dispatcher.Invoke(() => StatusMessage = $"❌ {msg}");

            // Загрузка списка устройств
            AvailableDevices = AudioRecordingService.GetAvailableDevices();
            if (AvailableDevices.Length > 0)
                SelectedDeviceIndex = AvailableDevices[0].index;

            _ = InitializeAsync();
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
            { Filter = "WAV файлы (*.wav)|*.wav|Все файлы|*.*" };
            if (dialog.ShowDialog() == true)
                SelectedFilePath = dialog.FileName;
        }

        // ✅ Автоматически генерирует AsyncRelayCommand с блокировкой параллельного запуска
        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task StartTranscriptionAsync()
        {
            if (string.IsNullOrEmpty(SelectedFilePath)) return;

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
                    modelName: "base",
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

        [RelayCommand(CanExecute = nameof(HasResult))]
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
                // Если выбрано устройство Loopback (индекс -100), используем useLoopback=true
                bool useLoopback = SelectedDeviceIndex == -100;
                int deviceIndex = useLoopback ? 0 : SelectedDeviceIndex;
                
                await _recordingService.StartRecordingAsync(deviceIndex, useLoopback);
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
