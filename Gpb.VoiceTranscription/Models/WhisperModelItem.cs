using System.ComponentModel;

namespace Gpb.VoiceTranscription.Models
{
    /// <summary>
    /// Модель Whisper-модели для привязки к ComboBox
    /// </summary>
    public class WhisperModelItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isDownloaded;

        public string Name { get; set; } = string.Empty;
        public Whisper.net.Ggml.GgmlType Type { get; set; }
        public string FileName { get; set; } = string.Empty;
        public bool IsDownloaded { 
            get => _isDownloaded; 
            set {
                if (_isDownloaded == value) return;
                _isDownloaded = value;
                OnPropertyChanged(nameof(IsDownloaded));
            } 
        }

        public string Description { get; set; } = string.Empty;

        public override string ToString() => $"{Name} ({(IsDownloaded ? "✓" : "↓")})";

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
