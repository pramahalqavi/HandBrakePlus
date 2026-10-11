// --------------------------------------------------------------------------------------------------------------------
// <copyright file="McpServer.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp.Mcp
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using HandBrake.Mcp.Protocol;
    using HandBrake.Mcp.Tools;

    public class McpServer
    {
        private readonly ToolRegistry toolRegistry;
        private readonly JsonSerializerOptions jsonOptions;

        public McpServer()
        {
            this.toolRegistry = new ToolRegistry();
            this.jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false); // UTF8 without BOM

            Console.Error.WriteLine("[HandBrake.Mcp] Server started. Listening on stdio (JSON-RPC 2.0)...");

            using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        // Stdio stream closed
                        Console.Error.WriteLine("[HandBrake.Mcp] Stdin closed, exiting server.");
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        await this.ProcessMessageAsync(line);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[HandBrake.Mcp] Unhandled exception processing line: {ex}");
                    }
                }
            }
        }

        private async Task ProcessMessageAsync(string jsonLine)
        {
            JsonRpcRequest request = null;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(jsonLine, this.jsonOptions);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HandBrake.Mcp] JSON parse error: {ex.Message}");
                var parseErr = JsonRpcResponse.Failure(null, JsonRpcErrorCodes.ParseError, "Parse error");
                await this.SendResponseAsync(parseErr);
                return;
            }

            if (request == null)
            {
                var invalidReq = JsonRpcResponse.Failure(null, JsonRpcErrorCodes.InvalidRequest, "Invalid request");
                await this.SendResponseAsync(invalidReq);
                return;
            }

            // Notification (id is null and method is a notification)
            if (request.Id == null)
            {
                this.HandleNotification(request);
                return;
            }

            // Method Request
            JsonRpcResponse response = null;
            switch (request.Method)
            {
                case "initialize":
                    response = this.HandleInitialize(request);
                    break;

                case "ping":
                    response = JsonRpcResponse.Success(request.Id, new { });
                    break;

                case "tools/list":
                    response = this.HandleToolsList(request);
                    break;

                case "tools/call":
                    response = this.HandleToolsCall(request);
                    break;

                default:
                    Console.Error.WriteLine($"[HandBrake.Mcp] Method not found: {request.Method}");
                    response = JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.MethodNotFound, $"Method '{request.Method}' not found.");
                    break;
            }

            if (response != null)
            {
                await this.SendResponseAsync(response);
            }
        }

        private void HandleNotification(JsonRpcRequest request)
        {
            if (request.Method == "notifications/initialized")
            {
                Console.Error.WriteLine("[HandBrake.Mcp] Client handshake initialized successfully.");
            }
            else
            {
                Console.Error.WriteLine($"[HandBrake.Mcp] Received notification: {request.Method}");
            }
        }

        private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
        {
            return JsonRpcResponse.Success(request.Id, new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = "handbrake-mcp",
                    version = "1.11.0"
                }
            });
        }

        private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
        {
            var tools = this.toolRegistry.GetToolList();
            return JsonRpcResponse.Success(request.Id, new
            {
                tools = tools
            });
        }

        private JsonRpcResponse HandleToolsCall(JsonRpcRequest request)
        {
            if (!request.Params.HasValue || request.Params.Value.ValueKind != JsonValueKind.Object)
            {
                return JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.InvalidParams, "Params must be an object with 'name' and 'arguments'.");
            }

            JsonElement paramsObj = request.Params.Value;
            if (!paramsObj.TryGetProperty("name", out var nameProp))
            {
                return JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.InvalidParams, "Tool 'name' is required.");
            }

            string toolName = nameProp.GetString();
            JsonElement? arguments = null;
            if (paramsObj.TryGetProperty("arguments", out var argsProp))
            {
                arguments = argsProp;
            }

            Console.Error.WriteLine($"[HandBrake.Mcp] Executing tool: '{toolName}'");
            McpToolCallResult callResult = this.toolRegistry.Execute(toolName, arguments);

            return JsonRpcResponse.Success(request.Id, callResult);
        }

        private async Task SendResponseAsync(JsonRpcResponse response)
        {
            string json = JsonSerializer.Serialize(response, this.jsonOptions);
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync();
        }
    }
}
