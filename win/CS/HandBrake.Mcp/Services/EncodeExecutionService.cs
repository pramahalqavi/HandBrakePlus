// --------------------------------------------------------------------------------------------------------------------
// <copyright file="EncodeExecutionService.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using HandBrake.Interop.Interop;
    using HandBrake.Interop.Interop.HbLib;
    using HandBrake.Interop.Interop.Helpers;
    using HandBrake.Interop.Interop.Json.Scan;
    using HandBrake.Interop.Interop.Json.State;
    using HandBrake.Interop.Utilities;

    public class EncodeOptions
    {
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string PresetName { get; set; } = "Fast 1080p30";
        public int TitleIndex { get; set; } = 0;
        public double? Rf { get; set; }
        public string VideoEncoder { get; set; }
        public string EncoderPreset { get; set; }
        public int? MaxWidth { get; set; }
        public int? MaxHeight { get; set; }
        public bool AllowUpscaling { get; set; } = false;
        public bool PreserveTimestamps { get; set; } = true;
        public bool Async { get; set; } = false;
    }

    public class EncodeJobStatus
    {
        public string JobId { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string State { get; set; } = "Pending"; // Pending, Running, Completed, Failed, Cancelled
        public double ProgressPercent { get; set; }
        public double CurrentFps { get; set; }
        public double AverageFps { get; set; }
        public int EtaSeconds { get; set; }
        public double ElapsedSeconds { get; set; }
        public long OutputSizeBytes { get; set; }
        public string Error { get; set; }

        internal CancellationTokenSource CancellationSource { get; set; }
        internal HandBrakeInstance RunningInstance { get; set; }
    }

    public class BatchEncodeResult
    {
        public string InputDirectory { get; set; }
        public string OutputDirectory { get; set; }
        public int TotalFiles { get; set; }
        public int CompletedFiles { get; set; }
        public int FailedFiles { get; set; }
        public double ElapsedSeconds { get; set; }
        public List<BatchFileResult> Results { get; set; } = new List<BatchFileResult>();
    }

    public class BatchFileResult
    {
        public string SourceFile { get; set; }
        public string DestinationFile { get; set; }
        public bool Success { get; set; }
        public double ElapsedSeconds { get; set; }
        public string Error { get; set; }
    }

    public class EncodeExecutionService
    {
        private static readonly ConcurrentDictionary<string, EncodeJobStatus> Jobs =
            new ConcurrentDictionary<string, EncodeJobStatus>(StringComparer.OrdinalIgnoreCase);

        public static EncodeJobStatus StartEncode(EncodeOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.SourcePath) || !File.Exists(options.SourcePath))
            {
                throw new FileNotFoundException($"Source video file not found: {options.SourcePath}");
            }

            if (string.IsNullOrWhiteSpace(options.DestinationPath))
            {
                throw new ArgumentException("Destination file path cannot be empty.");
            }

            string destDir = Path.GetDirectoryName(options.DestinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            string jobId = Guid.NewGuid().ToString("N");
            EncodeJobStatus status = new EncodeJobStatus
            {
                JobId = jobId,
                SourcePath = Path.GetFullPath(options.SourcePath),
                DestinationPath = Path.GetFullPath(options.DestinationPath),
                State = "Pending",
                CancellationSource = new CancellationTokenSource()
            };

            Jobs[jobId] = status;

            if (options.Async)
            {
                Task.Run(() => ExecuteEncode(status, options));
                return status;
            }
            else
            {
                ExecuteEncode(status, options);
                return status;
            }
        }

        public static EncodeJobStatus GetJobStatus(string jobId)
        {
            if (Jobs.TryGetValue(jobId, out var status))
            {
                return status;
            }

            return null;
        }

        public static bool CancelJob(string jobId)
        {
            if (Jobs.TryGetValue(jobId, out var status))
            {
                if (status.State == "Running" || status.State == "Pending")
                {
                    status.CancellationSource?.Cancel();
                    try
                    {
                        status.RunningInstance?.StopEncode();
                    }
                    catch
                    {
                        // Ignore stop errors
                    }

                    status.State = "Cancelled";
                    return true;
                }
            }

            return false;
        }

        public static BatchEncodeResult BatchEncode(
            string inputDir,
            string outputDir,
            string presetName = "Fast 1080p30",
            bool recursive = false,
            double? rf = null,
            bool preserveTimestamps = true,
            List<string> fileExtensions = null)
        {
            if (!Directory.Exists(inputDir))
            {
                throw new DirectoryNotFoundException($"Input directory not found: {inputDir}");
            }

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            if (fileExtensions == null || fileExtensions.Count == 0)
            {
                fileExtensions = new List<string> { "mp4", "mkv", "mov", "avi", "ts", "m4v" };
            }

            HashSet<string> validExtensions = new HashSet<string>(
                fileExtensions.Select(e => e.StartsWith(".") ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] allFiles = Directory.GetFiles(inputDir, "*.*", searchOption);
            List<string> videoFiles = allFiles.Where(f => validExtensions.Contains(Path.GetExtension(f))).ToList();

            BatchEncodeResult batchResult = new BatchEncodeResult
            {
                InputDirectory = inputDir,
                OutputDirectory = outputDir,
                TotalFiles = videoFiles.Count
            };

            Stopwatch batchTimer = Stopwatch.StartNew();

            foreach (string sourceFile in videoFiles)
            {
                Stopwatch fileTimer = Stopwatch.StartNew();
                string relativePath = Path.GetRelativePath(inputDir, sourceFile);
                string destFile = Path.Combine(outputDir, Path.ChangeExtension(relativePath, ".mp4"));
                string destFileDir = Path.GetDirectoryName(destFile);

                if (!string.IsNullOrEmpty(destFileDir) && !Directory.Exists(destFileDir))
                {
                    Directory.CreateDirectory(destFileDir);
                }

                EncodeOptions opts = new EncodeOptions
                {
                    SourcePath = sourceFile,
                    DestinationPath = destFile,
                    PresetName = presetName,
                    Rf = rf,
                    PreserveTimestamps = preserveTimestamps,
                    Async = false
                };

                BatchFileResult fileResult = new BatchFileResult
                {
                    SourceFile = sourceFile,
                    DestinationFile = destFile
                };

                try
                {
                    EncodeJobStatus jobStatus = StartEncode(opts);
                    fileResult.Success = (jobStatus.State == "Completed");
                    fileResult.Error = jobStatus.Error;
                    if (fileResult.Success)
                    {
                        batchResult.CompletedFiles++;
                    }
                    else
                    {
                        batchResult.FailedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    fileResult.Success = false;
                    fileResult.Error = ex.Message;
                    batchResult.FailedFiles++;
                }

                fileTimer.Stop();
                fileResult.ElapsedSeconds = Math.Round(fileTimer.Elapsed.TotalSeconds, 2);
                batchResult.Results.Add(fileResult);
            }

            batchTimer.Stop();
            batchResult.ElapsedSeconds = Math.Round(batchTimer.Elapsed.TotalSeconds, 2);
            return batchResult;
        }

        private static void ExecuteEncode(EncodeJobStatus status, EncodeOptions options)
        {
            DateTime? sourceCreationTimeUtc = null;
            DateTime? sourceLastWriteTimeUtc = null;

            try
            {
                if (options.PreserveTimestamps && File.Exists(options.SourcePath))
                {
                    FileInfo sourceFi = new FileInfo(options.SourcePath);
                    sourceCreationTimeUtc = sourceFi.CreationTimeUtc;
                    sourceLastWriteTimeUtc = sourceFi.LastWriteTimeUtc;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EncodeService] Warning: Could not read source timestamps: {ex.Message}");
            }

            HandBrakeUtils.EnsureGlobalInit(false);

            HandBrakeInstance instance = new HandBrakeInstance();
            instance.Initialize(verbosity: 0, noHardware: false);
            status.RunningInstance = instance;
            status.State = "Running";

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                // Step 1: Scan source file to populate libhb title set
                ManualResetEvent scanDone = new ManualResetEvent(false);
                instance.ScanCompleted += (s, e) => scanDone.Set();

                List<string> paths = new List<string> { options.SourcePath };
                instance.StartScan(paths, previewCount: 5, TimeSpan.FromSeconds(5), TimeSpan.Zero, 0,
                    new List<string> { "png", "jpg", "srt", "txt" }, hwDecode: 0, keepDuplicateTitles: false);

                if (!scanDone.WaitOne(TimeSpan.FromSeconds(60)))
                {
                    instance.StopScan();
                    throw new TimeoutException("Initial scan timed out.");
                }

                if (instance.Titles == null || instance.Titles.TitleList.Count == 0)
                {
                    throw new InvalidOperationException($"No valid titles found in: {options.SourcePath}");
                }

                int titleIndex = options.TitleIndex > 0 ? options.TitleIndex : instance.Titles.MainFeature;

                // Step 2: Get Preset JSON
                string presetJson = PresetService.GetPresetJson(options.PresetName) ?? PresetService.GetDefaultPresetJson();
                if (string.IsNullOrEmpty(presetJson))
                {
                    throw new InvalidOperationException($"Could not load preset: {options.PresetName}");
                }

                // Step 3: Initialize job JSON from libhb using preset
                IntPtr presetPtr = InteropUtilities.ToUtf8PtrFromString(presetJson);
                IntPtr jobJsonPtr = HBFunctions.hb_preset_job_init_json(instance.Handle, titleIndex, presetPtr);

                if (jobJsonPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to initialize job from preset in libhb.");
                }

                string jobJson = InteropUtilities.ToStringFromUtf8Ptr(jobJsonPtr);

                // Step 4: Parse job JSON and apply overrides
                JsonNode jobNode = JsonNode.Parse(jobJson);
                JsonObject jobObj = jobNode.AsObject();

                // Set Destination file
                if (jobObj["Destination"] is JsonObject destObj)
                {
                    destObj["File"] = options.DestinationPath;
                }

                // Apply Video Overrides
                if (jobObj["Video"] is JsonObject videoObj)
                {
                    if (options.Rf.HasValue)
                    {
                        videoObj["Quality"] = options.Rf.Value;
                        videoObj["QualityType"] = 2; // Constant Quality mode
                    }

                    if (!string.IsNullOrWhiteSpace(options.VideoEncoder))
                    {
                        videoObj["Encoder"] = options.VideoEncoder;
                    }

                    if (!string.IsNullOrWhiteSpace(options.EncoderPreset))
                    {
                        videoObj["Preset"] = options.EncoderPreset;
                    }
                }

                // Apply Dimension limits if specified
                if (options.MaxWidth.HasValue || options.MaxHeight.HasValue)
                {
                    if (jobObj["Destination"] is JsonObject dest)
                    {
                        if (options.MaxWidth.HasValue) dest["MaxWidth"] = options.MaxWidth.Value;
                        if (options.MaxHeight.HasValue) dest["MaxHeight"] = options.MaxHeight.Value;
                    }
                }

                string finalJobJson = jobObj.ToJsonString(JsonSettings.Options);

                // Step 5: Add Job to engine and start
                int addResult = HBFunctions.hb_add_json(instance.Handle, InteropUtilities.ToUtf8PtrFromString(finalJobJson));
                if (addResult < 0)
                {
                    throw new InvalidOperationException("Failed to add job to HandBrake engine queue.");
                }

                HBFunctions.hb_start(instance.Handle);

                // Step 6: Monitor progress loop
                while (!status.CancellationSource.IsCancellationRequested)
                {
                    Thread.Sleep(500);

                    IntPtr stateJsonPtr = HBFunctions.hb_get_state_json(instance.Handle);
                    string stateJson = Marshal.PtrToStringAnsi(stateJsonPtr);

                    if (!string.IsNullOrEmpty(stateJson))
                    {
                        try
                        {
                            JsonState jsonState = JsonSerializer.Deserialize<JsonState>(stateJson, JsonSettings.Options);
                            TaskState taskState = jsonState != null ? TaskState.FromRepositoryValue(jsonState.State) : null;

                            if (jsonState?.Working != null)
                            {
                                status.ProgressPercent = Math.Round(jsonState.Working.Progress * 100, 2);
                                status.CurrentFps = Math.Round(jsonState.Working.Rate, 1);
                                status.AverageFps = Math.Round(jsonState.Working.RateAvg, 1);
                                status.EtaSeconds = jsonState.Working.ETASeconds;
                            }

                            status.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);

                            if (taskState == TaskState.WorkDone)
                            {
                                if (jsonState.WorkDone != null && jsonState.WorkDone.Error != 0)
                                {
                                    status.State = "Failed";
                                    status.Error = $"HandBrake engine reported work error code: {jsonState.WorkDone.Error}";
                                }
                                else
                                {
                                    status.State = "Completed";
                                    status.ProgressPercent = 100;
                                }
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[EncodeService] Progress deserialize error: {ex.Message}");
                        }
                    }
                }

                if (status.CancellationSource.IsCancellationRequested)
                {
                    status.State = "Cancelled";
                }

                sw.Stop();
                status.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);

                // Step 7: Check output file and preserve timestamps
                if (status.State == "Completed" && File.Exists(options.DestinationPath))
                {
                    FileInfo destFi = new FileInfo(options.DestinationPath);
                    status.OutputSizeBytes = destFi.Length;

                    if (options.PreserveTimestamps && sourceCreationTimeUtc.HasValue && sourceLastWriteTimeUtc.HasValue)
                    {
                        try
                        {
                            File.SetCreationTimeUtc(options.DestinationPath, sourceCreationTimeUtc.Value);
                            File.SetLastWriteTimeUtc(options.DestinationPath, sourceLastWriteTimeUtc.Value);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[EncodeService] Warning: Could not preserve timestamps: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                status.State = "Failed";
                status.Error = ex.Message;
                status.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                Console.Error.WriteLine($"[EncodeService] Encode failed: {ex}");
            }
            finally
            {
                try
                {
                    instance.Dispose();
                }
                catch
                {
                    // Ignore dispose errors
                }
                status.RunningInstance = null;
            }
        }
    }
}
