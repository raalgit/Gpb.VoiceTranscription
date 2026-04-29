using Gpb.VoiceTranscription.Models;
using System;
using System.Collections.Generic;
using System.IO;
using Whisper.net.Ggml;

namespace Gpb.VoiceTranscription.Services
{
    public class ModelStorageService
    {
        // Вспомогательный метод для получения пути к файлу
        public static string GetModelPath(GgmlType modelType)
        {
            // Пример пути. Убедитесь, что он совпадает с тем, куда вы сохраняете файлы при скачивании.
            // Часто используют: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperModels", ...)
            var fileName = $"ggml-{modelType.ToString().ToLower()}.bin";
            // Или новый формат именования в новых версиях, если используется:
            // var fileName = $"ggml-${modelType.ToString().ToLower()}.q5_0.bin"; 

            // Для примера предположим, что модели хранятся в подпапке "Models" рядом с исполняемым файлом или в LocalApplicationData
            var folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperTranscriber", "Models");

            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            return Path.Combine(folderPath, fileName);
        }

        public static string GetModelDisplayName(GgmlType modelType)
        {
            return modelType switch
            {
                GgmlType.Tiny => "Tiny",
                GgmlType.Base => "Base",
                GgmlType.Small => "Small",
                GgmlType.Medium => "Medium",
                GgmlType.LargeV3 => "Large V3",
                _ => modelType.ToString()
            };
        }

        public static List<WhisperModelItem> GetAllModels()
        {
            // Список всех поддерживаемых типов моделей
            var allModelTypes = new[]
            {
                GgmlType.Tiny,
                GgmlType.Base,
                GgmlType.Small,
                GgmlType.Medium,
                GgmlType.LargeV3
                // Добавьте другие модели при необходимости (LargeV2, DistilLargeV3 и т.д.)
            };

            var modelsList = new List<WhisperModelItem>();

            foreach (var modelType in allModelTypes)
            {
                // Путь к файлу модели (логика должна совпадать с той, что используется при скачивании/загрузке)
                // Обычно это путь в %LOCALAPPDATA% или в папке приложения
                var modelPath = GetModelPath(modelType);

                bool isDownloaded = File.Exists(modelPath);

                modelsList.Add(new WhisperModelItem
                {
                    Type = modelType,
                    Name = GetModelDisplayName(modelType), // Например, "Tiny", "Base"
                    IsDownloaded = isDownloaded
                });
            }

            return modelsList;
        }
    }
}
