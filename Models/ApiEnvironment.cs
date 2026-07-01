using System.Collections.Generic;

namespace SwellSSH.Models
{
    public class ApiEnvironment
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "OpenAI";
        public string ProviderPreset { get; set; } = "OpenAI"; // OpenAI, DeepSeek, Ollama, etc.
        public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
        
        // Stored encrypted similar to connection passwords
        public string? EncryptedApiKey { get; set; }
        
        public List<string> AvailableModels { get; set; } = new List<string> { "gpt-4o-mini", "gpt-4o" };
        public string CurrentModel { get; set; } = "gpt-4o-mini";

        /// <summary>
        /// OpenAI reasoning_effort (e.g. low, medium, high). Empty means default.
        /// </summary>
        public string ReasoningEffort { get; set; } = "";

        public int ContextTokens { get; set; } = 128000;
        public int MaxRetries { get; set; } = 3;

        public string CustomUserAgent { get; set; } = "";
        public string HttpProxy { get; set; } = "";
    }
}
