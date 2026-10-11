// --------------------------------------------------------------------------------------------------------------------
// <copyright file="McpToolDefinition.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp.Protocol
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class McpTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("inputSchema")]
        public object InputSchema { get; set; }
    }

    public class McpToolCallResult
    {
        [JsonPropertyName("content")]
        public List<McpContent> Content { get; set; } = new List<McpContent>();

        [JsonPropertyName("isError")]
        public bool IsError { get; set; }

        public static McpToolCallResult Text(string text, bool isError = false)
        {
            return new McpToolCallResult
            {
                IsError = isError,
                Content = new List<McpContent>
                {
                    new McpContent { Type = "text", Text = text }
                }
            };
        }

        public static McpToolCallResult Json(object obj, bool isError = false)
        {
            string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            return Text(json, isError);
        }

        public static McpToolCallResult Error(string message)
        {
            return Text(message, isError: true);
        }
    }

    public class McpContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; set; }
    }
}
