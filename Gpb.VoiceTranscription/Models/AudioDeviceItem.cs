namespace Gpb.VoiceTranscription.Models
{
    /// <summary>
    /// Модель устройства для привязки к ComboBox
    /// </summary>
    public class AudioDeviceItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsLoopback { get; set; }
        public string? DeviceId { get; set; }
        public bool IsCombined { get; set; } // Флаг для комбинированной записи (микрофон + loopback)

        public override string ToString() => Name;
    }
}
