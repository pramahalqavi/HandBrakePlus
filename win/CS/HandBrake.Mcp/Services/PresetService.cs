// --------------------------------------------------------------------------------------------------------------------
// <copyright file="PresetService.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using HandBrake.Interop.Interop;
    using HandBrake.Interop.Interop.HbLib;
    using HandBrake.Interop.Interop.Json.Presets;
    using HandBrake.Interop.Utilities;

    public class PresetInfo
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public string Format { get; set; }
        public bool IsDefault { get; set; }
    }

    public class PresetService
    {
        private static readonly object lockObj = new object();
        private static List<PresetInfo> cachedPresetInfos = null;
        private static Dictionary<string, (HBPreset Preset, string Category)> cachedPresetsByName = null;

        public static void EnsureLoaded()
        {
            lock (lockObj)
            {
                if (cachedPresetInfos != null) return;

                HandBrakeUtils.EnsureGlobalInit(false);
                HBFunctions.hb_presets_builtin_init();

                cachedPresetInfos = new List<PresetInfo>();
                cachedPresetsByName = new Dictionary<string, (HBPreset, string)>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    IList<HBPresetCategory> builtinCategories = HandBrakePresetService.GetBuiltInPresets();
                    if (builtinCategories != null)
                    {
                        foreach (HBPresetCategory cat in builtinCategories)
                        {
                            AddCategoryPresets(cat.PresetName, cat.ChildrenArray);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PresetService] Error loading built-in presets: {ex.Message}");
                }

                // Load user presets from AppData
                try
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string userPresetsPath = Path.Combine(appData, "HandBrake", "user_presets.json");
                    if (File.Exists(userPresetsPath))
                    {
                        PresetTransportContainer userPresets = HandBrakePresetService.GetPresetsFromFile(userPresetsPath);
                        if (userPresets?.PresetList != null)
                        {
                            foreach (var item in userPresets.PresetList)
                            {
                                try
                                {
                                    string json = item?.ToString();
                                    if (string.IsNullOrEmpty(json)) continue;

                                    var cat = JsonSerializer.Deserialize<HBPresetCategory>(json, JsonSettings.Options);
                                    if (cat != null && cat.Folder && cat.ChildrenArray != null)
                                    {
                                        AddCategoryPresets(cat.PresetName, cat.ChildrenArray);
                                        continue;
                                    }

                                    var p = JsonSerializer.Deserialize<HBPreset>(json, JsonSettings.Options);
                                    if (p != null && !p.Folder)
                                    {
                                        AddPreset("Custom", p);
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PresetService] Error loading user presets: {ex.Message}");
                }
            }
        }

        private static void AddCategoryPresets(string categoryName, List<HBPreset> presets)
        {
            if (presets == null) return;
            foreach (HBPreset preset in presets)
            {
                AddPreset(categoryName, preset);
            }
        }

        private static void AddPreset(string categoryName, HBPreset preset)
        {
            if (string.IsNullOrEmpty(preset.PresetName)) return;

            if (!cachedPresetsByName.ContainsKey(preset.PresetName))
            {
                cachedPresetsByName[preset.PresetName] = (preset, categoryName);
                cachedPresetInfos.Add(new PresetInfo
                {
                    Name = preset.PresetName,
                    Category = categoryName,
                    Description = preset.PresetDescription,
                    Format = preset.FileFormat,
                    IsDefault = preset.Default
                });
            }
        }

        public static List<PresetInfo> ListPresets(string category = null, string search = null)
        {
            EnsureLoaded();

            IEnumerable<PresetInfo> query = cachedPresetInfos;

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(p =>
                    p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (p.Description != null && p.Description.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            return query.OrderBy(p => p.Category).ThenBy(p => p.Name).ToList();
        }

        public static HBPreset GetPreset(string name)
        {
            EnsureLoaded();

            if (cachedPresetsByName.TryGetValue(name, out var item))
            {
                return item.Preset;
            }

            return null;
        }

        public static string GetPresetJson(string name)
        {
            EnsureLoaded();

            HBPreset preset = GetPreset(name);
            if (preset != null)
            {
                return JsonSerializer.Serialize(preset, JsonSettings.Options);
            }

            // Fallback to default preset from hb
            IntPtr defaultPresetPtr = HBFunctions.hb_presets_get_default_json();
            if (defaultPresetPtr != IntPtr.Zero)
            {
                return Marshal.PtrToStringUTF8(defaultPresetPtr);
            }

            return null;
        }

        public static string GetDefaultPresetJson()
        {
            EnsureLoaded();

            // Try "Fast 1080p30" or first default preset
            var defaultPreset = cachedPresetInfos.FirstOrDefault(p => p.IsDefault) ??
                                cachedPresetInfos.FirstOrDefault(p => string.Equals(p.Name, "Fast 1080p30", StringComparison.OrdinalIgnoreCase)) ??
                                cachedPresetInfos.FirstOrDefault();

            if (defaultPreset != null)
            {
                return GetPresetJson(defaultPreset.Name);
            }

            IntPtr defaultPresetPtr = HBFunctions.hb_presets_get_default_json();
            if (defaultPresetPtr != IntPtr.Zero)
            {
                return Marshal.PtrToStringUTF8(defaultPresetPtr);
            }

            return null;
        }
    }
}
