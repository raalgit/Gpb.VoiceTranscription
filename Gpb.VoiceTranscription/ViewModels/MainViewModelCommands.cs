using CommunityToolkit.Mvvm.Input;
using Gpb.VoiceTranscription.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Gpb.VoiceTranscription.ViewModels
{
    public partial class MainViewModel
    {
        #region Commands

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
            if (!AudioConverterHelper.IsSupportedAudioFormat(SelectedFilePath))
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

                    if (!AudioConverterHelper.IsWhisperCompatible(SelectedFilePath))
                    {
                        StatusMessage = "⚙️ Конвертация в 16kHz Mono WAV...";
                        await AudioConverterHelper.ConvertToWhisperFormatAsync(SelectedFilePath, tempFilePath);
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

                await LoadSelectedModelAsync();
                var transcription = await _transcriptionService.TranscribeAsync(
                    processPath,
                    modelName: SelectedModel.Name,
                    useChunkingForLargeSize: UseChunkingForLargeFiles,
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

        [RelayCommand]
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
                bool isCombined = selectedDevice?.IsCombined == true;
                int deviceIndex = (useLoopback || isCombined) ? 0 : SelectedDeviceIndex;

                await _recordingService.StartRecordingAsync(deviceIndex, useLoopback, SelectedLoopbackDeviceId, isCombined);
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
                await DownloadModelAsync(SelectedModel.Type);
            }

            // После скачивания (или если модель уже была скачана) загружаем её
            if (SelectedModel.IsDownloaded)
            {
                await LoadSelectedModelAsync();
            }
        }
        #endregion
    }
}
