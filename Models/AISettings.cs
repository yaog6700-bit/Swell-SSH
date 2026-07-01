using System.Collections.Generic;

namespace SwellSSH.Models
{
    public class AISettings
    {
        public bool IsEnabled { get; set; } = true;
        
        public List<ApiEnvironment> Environments { get; set; } = new List<ApiEnvironment>();
        
        public string CurrentEnvironmentId { get; set; } = "";

        /// <summary>
        /// Command confirm strategy: "Strict" | "Balanced" | "None"
        /// </summary>
        public string ConfirmStrategy { get; set; } = "Balanced";

        /// <summary>
        /// Default execution mode: "Background" | "Shell"
        /// </summary>
        public string ExecutionMethod { get; set; } = "Background";

        public int CommandTimeoutSeconds { get; set; } = 30;
        public int ContextLineCount { get; set; } = 50;

        public string CustomPrompt { get; set; } = "";
    }
}
