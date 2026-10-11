// --------------------------------------------------------------------------------------------------------------------
// <copyright file="MediaScanService.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using HandBrake.Interop.Interop;
    using HandBrake.Interop.Interop.Json.Scan;

    public class MediaScanResult
    {
        public string SourcePath { get; set; }
        public int TitleCount { get; set; }
        public int MainTitleIndex { get; set; }
        public List<TitleSummary> Titles { get; set; } = new List<TitleSummary>();
    }

    public class TitleSummary
    {
        public int Index { get; set; }
        public bool IsMainTitle { get; set; }
        public string DurationFormatted { get; set; }
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string AspectRatio { get; set; }
        public string PixelAspectRatio { get; set; }
        public string VideoCodec { get; set; }
        public double FrameRate { get; set; }
        public string ColorProfile { get; set; }
        public List<AudioSummary> AudioTracks { get; set; } = new List<AudioSummary>();
        public List<SubtitleSummary> Subtitles { get; set; } = new List<SubtitleSummary>();
        public List<ChapterSummary> Chapters { get; set; } = new List<ChapterSummary>();
    }

    public class AudioSummary
    {
        public int TrackNumber { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public string LanguageCode { get; set; }
        public string Description { get; set; }
        public string CodecName { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        public int ChannelCount { get; set; }
        public string ChannelLayoutName { get; set; }
    }

    public class SubtitleSummary
    {
        public int TrackNumber { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public string LanguageCode { get; set; }
        public string Format { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
    }

    public class ChapterSummary
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public string DurationFormatted { get; set; }
        public double DurationSeconds { get; set; }
    }

    public class MediaScanService
    {
        public static MediaScanResult Scan(string path, int minDurationSeconds = 10, int timeoutSeconds = 60)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new FileNotFoundException($"Media path does not exist: {path}");
            }

            HandBrakeUtils.EnsureGlobalInit(false);

            HandBrakeInstance instance = new HandBrakeInstance();
            instance.Initialize(verbosity: 0, noHardware: false);

            try
            {
                ManualResetEvent scanDoneEvent = new ManualResetEvent(false);

                instance.ScanCompleted += (s, e) =>
                {
                    scanDoneEvent.Set();
                };

                List<string> paths = new List<string> { path };
                TimeSpan minDuration = TimeSpan.FromSeconds(minDurationSeconds);
                TimeSpan maxDuration = TimeSpan.Zero; // 0 = unlimited

                List<string> excludedExtensions = new List<string> { "png", "jpg", "srt", "txt", "nfo" };
                instance.StartScan(paths, previewCount: 5, minDuration, maxDuration, titleIndex: 0, excludedExtensions, hwDecode: 0, keepDuplicateTitles: false);

                bool completed = scanDoneEvent.WaitOne(TimeSpan.FromSeconds(timeoutSeconds));
                if (!completed)
                {
                    instance.StopScan();
                    throw new TimeoutException($"Media scan timed out after {timeoutSeconds} seconds for: {path}");
                }

                if (instance.Titles == null || instance.Titles.TitleList == null)
                {
                    throw new InvalidOperationException($"No valid titles or video streams found in: {path}");
                }

                MediaScanResult result = new MediaScanResult
                {
                    SourcePath = path,
                    TitleCount = instance.Titles.TitleList.Count,
                    MainTitleIndex = instance.Titles.MainFeature
                };

                foreach (SourceTitle title in instance.Titles.TitleList)
                {
                    TitleSummary summary = new TitleSummary
                    {
                        Index = title.Index,
                        IsMainTitle = (title.Index == instance.Titles.MainFeature),
                        Width = title.Geometry?.Width ?? 0,
                        Height = title.Geometry?.Height ?? 0,
                        AspectRatio = title.Geometry?.PAR != null ? $"{title.Geometry.PAR.Num}:{title.Geometry.PAR.Den}" : null,
                        PixelAspectRatio = title.Geometry?.PAR != null ? $"{title.Geometry.PAR.Num}/{title.Geometry.PAR.Den}" : null,
                        VideoCodec = title.VideoCodec,
                        FrameRate = (title.FrameRate != null && title.FrameRate.Num > 0 && title.FrameRate.Den > 0)
                            ? (double)title.FrameRate.Num / title.FrameRate.Den
                            : 0,
                        ColorProfile = title.Color?.ToString(),
                        DurationSeconds = title.Duration.Hours * 3600 + title.Duration.Minutes * 60 + title.Duration.Seconds,
                        DurationFormatted = $"{title.Duration.Hours:D2}:{title.Duration.Minutes:D2}:{title.Duration.Seconds:D2}"
                    };

                    if (title.AudioList != null)
                    {
                        foreach (SourceAudioTrack audio in title.AudioList)
                        {
                            summary.AudioTracks.Add(new AudioSummary
                            {
                                TrackNumber = audio.TrackNumber,
                                Name = audio.Name,
                                Language = audio.Language,
                                LanguageCode = audio.LanguageCode,
                                Description = audio.Description,
                                CodecName = audio.CodecName,
                                BitRate = audio.BitRate,
                                SampleRate = audio.SampleRate,
                                ChannelCount = audio.ChannelCount,
                                ChannelLayoutName = audio.ChannelLayoutName
                            });
                        }
                    }

                    if (title.SubtitleList != null)
                    {
                        foreach (SourceSubtitleTrack sub in title.SubtitleList)
                        {
                            summary.Subtitles.Add(new SubtitleSummary
                            {
                                TrackNumber = sub.TrackNumber,
                                Name = sub.Name,
                                Language = sub.Language,
                                LanguageCode = sub.LanguageCode,
                                Format = sub.Format,
                                IsDefault = sub.Attributes?.Default ?? false,
                                IsForced = sub.Attributes?.Forced ?? false
                            });
                        }
                    }

                    if (title.ChapterList != null)
                    {
                        int chIdx = 1;
                        foreach (SourceChapter ch in title.ChapterList)
                        {
                            double chSec = ch.Duration.Hours * 3600 + ch.Duration.Minutes * 60 + ch.Duration.Seconds;
                            summary.Chapters.Add(new ChapterSummary
                            {
                                Number = chIdx++,
                                Name = ch.Name,
                                DurationSeconds = chSec,
                                DurationFormatted = $"{ch.Duration.Hours:D2}:{ch.Duration.Minutes:D2}:{ch.Duration.Seconds:D2}"
                            });
                        }
                    }

                    result.Titles.Add(summary);
                }

                return result;
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
            }
        }
    }
}
