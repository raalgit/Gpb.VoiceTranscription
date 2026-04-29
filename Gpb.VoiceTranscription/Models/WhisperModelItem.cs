namespace Gpb.VoiceTranscription.Models
{
    /// <summary>
    /// Модель Whisper-модели для привязки к ComboBox
    /// </summary>
    public class WhisperModelItem
    {
        public string Name { get; set; } = string.Empty;
        public Whisper.net.Ggml.GgmlType Type { get; set; }
        public string FileName { get; set; } = string.Empty;
        public bool IsDownloaded { get; set; }
        public string Description { get; set; } = string.Empty;

        public override string ToString() => $"{Name} ({(IsDownloaded ? "✓" : "↓")})";
    }
}
