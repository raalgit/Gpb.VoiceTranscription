using Gpb.VoiceTranscription.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace Gpb.VoiceTranscription
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        public MainWindow()
        {
            InitializeComponent();

            // Путь к модели (скачается автоматически при первом запуске)
            var modelPath = Path.Combine(
                Environment.CurrentDirectory,
                "Assets", "Models", "ggml-base.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

            _viewModel = new MainViewModel(modelPath);
            DataContext = _viewModel;

            // Показываем результат при его появлении
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.TranscriptionResult) &&
                    !string.IsNullOrEmpty(_viewModel.TranscriptionResult))
                {
                    ResultGrid.Visibility = Visibility.Visible;
                }
            };
        }

        private void Border_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Border_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                var wavFile = files.FirstOrDefault(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(wavFile))
                {
                    _viewModel.SelectedFilePath = wavFile;
                }
                else
                {
                    MessageBox.Show("Пожалуйста, выберите файл в формате .wav", "Неверный формат",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ValueTuple<int, string, bool, string?> device)
            {
                _viewModel.GetType().GetProperty(nameof(MainViewModel.SelectedLoopbackDeviceId))
                    ?.SetValue(_viewModel, device.Item2);
            }
        }
    }
}