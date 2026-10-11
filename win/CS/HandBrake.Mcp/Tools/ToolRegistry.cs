// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ToolRegistry.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using HandBrake.Interop.Interop;
    using HandBrake.Mcp.Protocol;
    using HandBrake.Mcp.Services;

    public class ToolRegistry
    {
        private readonly Dictionary<string, (McpTool Tool, Func<JsonElement?, McpToolCallResult> Handler)> tools =
            new Dictionary<string, (McpTool, Func<JsonElement?, McpToolCallResult>)>(StringComparer.OrdinalIgnoreCase);

        public ToolRegistry()
        {
            this.RegisterTools();
        }

        public List<McpTool> GetToolList()
        {
            return this.tools.Values.Select(v => v.Tool).ToList();
        }

        public McpToolCallResult Execute(string toolName, JsonElement? arguments)
        {
            if (this.tools.TryGetValue(toolName, out var entry))
            {
                try
                {
                    return entry.Handler(arguments);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ToolRegistry] Error executing '{toolName}': {ex}");
                    return McpToolCallResult.Error($"Error executing tool '{toolName}': {ex.Message}");
                }
            }

            return McpToolCallResult.Error($"Unknown tool: '{toolName}'");
        }

        private void RegisterTools()
        {
            // 1. handbrake_system_info
            this.Register(
                new McpTool
                {
                    Name = "handbrake_system_info",
                    Description = "Get HandBrake engine version, build number, CPU capabilities, and available GPU hardware acceleration status (NVENC, QuickSync, AMD AMF).",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>()
                    }
                },
                args =>
                {
                    HandBrakeUtils.EnsureGlobalInit(false);
                    return McpToolCallResult.Json(new
                    {
                        version = "1.11.0",
                        build = 1,
                        hardware_acceleration = new
                        {
                            nvdec_available = HandBrakeHardwareEncoderHelper.IsNVDecAvailable,
                            nvenc_available = HandBrakeHardwareEncoderHelper.IsNVEncH264Available || HandBrakeHardwareEncoderHelper.IsNVEncH265Available,
                            qsv_available = HandBrakeHardwareEncoderHelper.IsQsvAvailable,
                            amf_available = HandBrakeHardwareEncoderHelper.IsAMFDecAvailable || HandBrakeHardwareEncoderHelper.IsVceH264Available || HandBrakeHardwareEncoderHelper.IsVceH265Available,
                            directx_available = HandBrakeHardwareEncoderHelper.IsDirectXAvailable
                        },
                        cpu_cores = Environment.ProcessorCount,
                        os = Environment.OSVersion.ToString()
                    });
                });

            // 2. handbrake_scan
            this.Register(
                new McpTool
                {
                    Name = "handbrake_scan",
                    Description = "Scan a video file or folder to retrieve detailed technical metadata: duration, resolutions, aspect ratio, frame rate, video codec, audio tracks (codec, bitrate, language, channel layout), subtitle tracks, and chapters.",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Absolute file path to the video file or folder." },
                            min_duration = new { type = "integer", description = "Optional minimum title duration in seconds to consider (default: 10)." }
                        },
                        required = new[] { "path" }
                    }
                },
                args =>
                {
                    string path = GetStringArg(args, "path");
                    int minDuration = GetIntArg(args, "min_duration", 10);

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return McpToolCallResult.Error("Argument 'path' is required.");
                    }

                    MediaScanResult result = MediaScanService.Scan(path, minDuration);
                    return McpToolCallResult.Json(result);
                });

            // 3. handbrake_list_presets
            this.Register(
                new McpTool
                {
                    Name = "handbrake_list_presets",
                    Description = "List available built-in and user presets in HandBrake, optionally filtered by category or search term.",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string", description = "Optional category name (e.g., 'General', 'Web', 'Devices', 'Matroska', 'Hardware', 'Production')." },
                            search = new { type = "string", description = "Optional search query to filter presets by name or description." }
                        },
                        required = Array.Empty<string>()
                    }
                },
                args =>
                {
                    string category = GetStringArg(args, "category");
                    string search = GetStringArg(args, "search");

                    var presets = PresetService.ListPresets(category, search);
                    return McpToolCallResult.Json(new
                    {
                        count = presets.Count,
                        presets = presets
                    });
                });

            // 4. handbrake_get_preset
            this.Register(
                new McpTool
                {
                    Name = "handbrake_get_preset",
                    Description = "Retrieve complete configuration settings for a specific named preset (e.g. 'Fast 1080p30', 'HQ 1080p30 Surround', 'Creator 2160p60 4K').",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Exact name of the preset to inspect." }
                        },
                        required = new[] { "name" }
                    }
                },
                args =>
                {
                    string name = GetStringArg(args, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return McpToolCallResult.Error("Argument 'name' is required.");
                    }

                    var preset = PresetService.GetPreset(name);
                    if (preset == null)
                    {
                        return McpToolCallResult.Error($"Preset '{name}' was not found.");
                    }

                    return McpToolCallResult.Json(preset);
                });

            // 5. handbrake_list_encoders
            this.Register(
                new McpTool
                {
                    Name = "handbrake_list_encoders",
                    Description = "List all supported video encoders (x264, x265, x265 10-bit, SVT-AV1, NVENC, QSV, AMF) and audio encoders available in the engine.",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>()
                    }
                },
                args =>
                {
                    HandBrakeUtils.EnsureGlobalInit(false);

                    var videoEncoders = HandBrakeEncoderHelpers.VideoEncoders.Select(e => new
                    {
                        id = e.Id,
                        name = e.DisplayName,
                        short_name = e.ShortName,
                        is_hardware = e.IsHardwareEncoder
                    }).ToList();

                    var audioEncoders = HandBrakeEncoderHelpers.AudioEncoders.Select(a => new
                    {
                        id = a.Id,
                        name = a.DisplayName,
                        short_name = a.ShortName,
                        is_passthru = a.IsPassthru
                    }).ToList();

                    var mixdowns = HandBrakeEncoderHelpers.Mixdowns.Select(m => new
                    {
                        id = m.Id,
                        name = m.DisplayName,
                        short_name = m.ShortName
                    }).ToList();

                    return McpToolCallResult.Json(new
                    {
                        video_encoders = videoEncoders,
                        audio_encoders = audioEncoders,
                        mixdowns = mixdowns
                    });
                });

            // 6. handbrake_encode
            this.Register(
                new McpTool
                {
                    Name = "handbrake_encode",
                    Description = "Encode a video file using a HandBrake preset and optional parameter overrides (RF quality, video encoder, speed preset, resolution limits, allow upscaling, timestamp preservation).",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            source = new { type = "string", description = "Absolute path to the source video file." },
                            destination = new { type = "string", description = "Absolute path for the output file (e.g. C:\\Output\\video.mp4)." },
                            preset = new { type = "string", description = "Name of the preset to use (default: 'Fast 1080p30')." },
                            title_index = new { type = "integer", description = "Title index to encode (default: 0 for main feature)." },
                            rf = new { type = "number", description = "Constant Quality RF override (e.g., 20.0, 22.0, 28.0). Lower is higher quality." },
                            video_encoder = new { type = "string", description = "Video encoder override (e.g. 'x264', 'x265', 'x265_10bit', 'nvenc_h265')." },
                            encoder_preset = new { type = "string", description = "Encoder speed preset override (e.g. 'veryfast', 'fast', 'medium', 'slow')." },
                            max_width = new { type = "integer", description = "Optional maximum output width in pixels." },
                            max_height = new { type = "integer", description = "Optional maximum output height in pixels." },
                            allow_upscaling = new { type = "boolean", description = "Allow upscaling above source dimensions (default: false)." },
                            preserve_timestamps = new { type = "boolean", description = "Preserve creation and modified dates from source file (default: true)." },
                            @async = new { type = "boolean", description = "If true, starts encoding in background and returns job_id immediately. If false, blocks until encoding finishes (default: false)." }
                        },
                        required = new[] { "source", "destination" }
                    }
                },
                args =>
                {
                    string source = GetStringArg(args, "source");
                    string destination = GetStringArg(args, "destination");

                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
                    {
                        return McpToolCallResult.Error("Arguments 'source' and 'destination' are required.");
                    }

                    EncodeOptions opts = new EncodeOptions
                    {
                        SourcePath = source,
                        DestinationPath = destination,
                        PresetName = GetStringArg(args, "preset") ?? "Fast 1080p30",
                        TitleIndex = GetIntArg(args, "title_index", 0),
                        Rf = GetDoubleArg(args, "rf"),
                        VideoEncoder = GetStringArg(args, "video_encoder"),
                        EncoderPreset = GetStringArg(args, "encoder_preset"),
                        MaxWidth = GetNullableIntArg(args, "max_width"),
                        MaxHeight = GetNullableIntArg(args, "max_height"),
                        AllowUpscaling = GetBoolArg(args, "allow_upscaling", false),
                        PreserveTimestamps = GetBoolArg(args, "preserve_timestamps", true),
                        Async = GetBoolArg(args, "async", false)
                    };

                    EncodeJobStatus status = EncodeExecutionService.StartEncode(opts);
                    return McpToolCallResult.Json(status);
                });

            // 7. handbrake_batch_encode
            this.Register(
                new McpTool
                {
                    Name = "handbrake_batch_encode",
                    Description = "Batch encode all video files in a directory into a destination directory using a specified preset and settings.",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            input_directory = new { type = "string", description = "Source folder containing video files." },
                            output_directory = new { type = "string", description = "Destination folder for converted videos." },
                            preset = new { type = "string", description = "Name of the preset to use (default: 'Fast 1080p30')." },
                            recursive = new { type = "boolean", description = "Scan subfolders recursively (default: false)." },
                            rf = new { type = "number", description = "Optional Constant Quality RF override." },
                            preserve_timestamps = new { type = "boolean", description = "Preserve source creation/modified timestamps (default: true)." },
                            file_extensions = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "List of file extensions to include without leading dot (default: ['mp4', 'mkv', 'mov', 'avi', 'ts', 'm4v'])."
                            }
                        },
                        required = new[] { "input_directory", "output_directory" }
                    }
                },
                args =>
                {
                    string inputDir = GetStringArg(args, "input_directory");
                    string outputDir = GetStringArg(args, "output_directory");

                    if (string.IsNullOrWhiteSpace(inputDir) || string.IsNullOrWhiteSpace(outputDir))
                    {
                        return McpToolCallResult.Error("Arguments 'input_directory' and 'output_directory' are required.");
                    }

                    string preset = GetStringArg(args, "preset") ?? "Fast 1080p30";
                    bool recursive = GetBoolArg(args, "recursive", false);
                    double? rf = GetDoubleArg(args, "rf");
                    bool preserveTimestamps = GetBoolArg(args, "preserve_timestamps", true);
                    List<string> extensions = GetStringListArg(args, "file_extensions");

                    BatchEncodeResult result = EncodeExecutionService.BatchEncode(
                        inputDir, outputDir, preset, recursive, rf, preserveTimestamps, extensions);

                    return McpToolCallResult.Json(result);
                });

            // 8. handbrake_get_job_status
            this.Register(
                new McpTool
                {
                    Name = "handbrake_get_job_status",
                    Description = "Get the progress, speed (FPS), remaining time (ETA), and state of an asynchronous encode job.",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            job_id = new { type = "string", description = "The ID of the encode job." }
                        },
                        required = new[] { "job_id" }
                    }
                },
                args =>
                {
                    string jobId = GetStringArg(args, "job_id");
                    if (string.IsNullOrWhiteSpace(jobId))
                    {
                        return McpToolCallResult.Error("Argument 'job_id' is required.");
                    }

                    var status = EncodeExecutionService.GetJobStatus(jobId);
                    if (status == null)
                    {
                        return McpToolCallResult.Error($"Job with ID '{jobId}' was not found.");
                    }

                    return McpToolCallResult.Json(status);
                });

            // 9. handbrake_cancel_job
            this.Register(
                new McpTool
                {
                    Name = "handbrake_cancel_job",
                    Description = "Cancel an ongoing asynchronous encoding job.",
                    InputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            job_id = new { type = "string", description = "The ID of the job to cancel." }
                        },
                        required = new[] { "job_id" }
                    }
                },
                args =>
                {
                    string jobId = GetStringArg(args, "job_id");
                    if (string.IsNullOrWhiteSpace(jobId))
                    {
                        return McpToolCallResult.Error("Argument 'job_id' is required.");
                    }

                    bool cancelled = EncodeExecutionService.CancelJob(jobId);
                    return McpToolCallResult.Json(new
                    {
                        job_id = jobId,
                        success = cancelled,
                        message = cancelled ? "Job cancelled successfully." : "Job could not be cancelled (it may have already completed or was not found)."
                    });
                });
        }

        private void Register(McpTool tool, Func<JsonElement?, McpToolCallResult> handler)
        {
            this.tools[tool.Name] = (tool, handler);
        }

        private static string GetStringArg(JsonElement? args, string name)
        {
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty(name, out var prop))
            {
                return prop.GetString();
            }
            return null;
        }

        private static int GetIntArg(JsonElement? args, string name, int defaultValue)
        {
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty(name, out var prop))
            {
                if (prop.TryGetInt32(out int val)) return val;
            }
            return defaultValue;
        }

        private static int? GetNullableIntArg(JsonElement? args, string name)
        {
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty(name, out var prop))
            {
                if (prop.TryGetInt32(out int val)) return val;
            }
            return null;
        }

        private static double? GetDoubleArg(JsonElement? args, string name)
        {
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty(name, out var prop))
            {
                if (prop.TryGetDouble(out double val)) return val;
            }
            return null;
        }

        private static bool GetBoolArg(JsonElement? args, string name, bool defaultValue)
        {
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                {
                    return prop.GetBoolean();
                }
            }
            return defaultValue;
        }

        private static List<string> GetStringListArg(JsonElement? args, string name)
        {
            if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    List<string> list = new List<string>();
                    foreach (var item in prop.EnumerateArray())
                    {
                        string str = item.GetString();
                        if (!string.IsNullOrEmpty(str)) list.Add(str);
                    }
                    return list;
                }
            }
            return null;
        }
    }
}
