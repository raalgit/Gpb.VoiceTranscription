using Gpb.VoiceTranscription.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Gpb.VoiceTranscription.Services
{
    /// <summary>
    /// Сервис для суммаризации текста транскрибации с использованием LLM API
    /// </summary>
    public interface ISummarizationService
    {
        /// <summary>
        /// Суммаризировать текст транскрибации
        /// </summary>
        /// <param name="transcriptionText">Текст транскрибации</param>
        /// <param name="cancellationToken">Токен отмены</param>
        /// <returns>Суммаризированный текст</returns>
        Task<string> SummarizeAsync(string transcriptionText, CancellationToken cancellationToken = default);

        /// <summary>
        /// Проверка доступности сервиса суммаризации
        /// </summary>
        bool IsAvailable { get; }
    }

    public class SummarizationService : ISummarizationService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _modelName;
        private readonly int _maxTextLength;

        public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

        public SummarizationService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };
            _apiKey = AppSettings.SummarizationApiKey;
            _endpoint = AppSettings.SummarizationEndpoint;
            _modelName = AppSettings.SummarizationModel;
            _maxTextLength = AppSettings.SummarizationMaxTextLength;
        }

        public async Task<string> SummarizeAsync(string transcriptionText, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable)
                throw new InvalidOperationException("API ключ для суммаризации не настроен. Укажите SummarizationApiKey в конфиге.");

            if (string.IsNullOrEmpty(transcriptionText))
                return string.Empty;

            // Обрезаем текст если он слишком длинный
            if (transcriptionText.Length > _maxTextLength)
            {
                transcriptionText = transcriptionText.Substring(0, _maxTextLength) + "...";
            }

            var requestPayload = new
            {
                model = _modelName,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "Ты - профессиональный ассистент для суммаризации совещаний. Твоя задача - создать краткое, информативное резюме встречи на основе транскрибации. Выдели ключевые темы, решения, действия и ответственных. Пиши на том же языке, что и исходный текст."
                    },
                    new
                    {
                        role = "user",
                        content = $"Суммаризируй следующую транскрибацию совещания:\n\n{transcriptionText}"
                    }
                },
                temperature = 0.3,
                max_tokens = 1000
            };

            var jsonContent = JsonSerializer.Serialize(requestPayload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.PostAsync(_endpoint, httpContent, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Ошибка суммаризации: {response.StatusCode}. {errorContent}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            
            var summary = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return summary ?? "Не удалось получить суммаризацию";
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
