using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SparkXPrintConsole
{
    public class PrinterService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly string CacheFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history_cache.json");
        private static readonly string BreakdownCacheFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "feature_breakdown_cache.json");
        private static readonly ConcurrentDictionary<string, FileMetadata> ActiveFileMetaCache = new();

        public static async Task<LivePrinterStatus> GetLiveStatusAsync(string ip, int port, Dictionary<string, string>? thumbCache = null)
        {
            var status = new LivePrinterStatus { IsConnected = false, State = "offline", StateLabel = "Disconnessa" };
            try
            {
                var url = $"http://{ip}:{port}/printer/objects/query?print_stats&extruder&heater_bed&virtual_sdcard&idle_timeout&display_status";
                var res = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(res);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("status", out var st))
                {
                    status.IsConnected = true;

                    string rawState = "ready";
                    if (st.TryGetProperty("print_stats", out var ps))
                    {
                        rawState = ps.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "ready" : "ready";
                        status.Filename = ps.TryGetProperty("filename", out var fEl) ? fEl.GetString() ?? "" : "";

                        if (ps.TryGetProperty("print_duration", out var durEl))
                        {
                            status.PrintDurationSec = durEl.GetDouble();
                            status.PrintDurationStr = FormatDuration(status.PrintDurationSec);
                        }

                        if (ps.TryGetProperty("filament_used", out var filEl))
                        {
                            var mm = filEl.GetDouble();
                            status.FilamentUsedM = Math.Round(mm / 1000.0, 2);
                            status.FilamentUsedG = Math.Round((mm * Math.PI * Math.Pow(1.75 / 2.0, 2) / 1000.0) * 1.24, 1);
                        }

                        if (ps.TryGetProperty("z_pos", out var zEl))
                        {
                            status.CurrentZ = Math.Round(zEl.GetDouble(), 2);
                        }

                        if (ps.TryGetProperty("info", out var infoEl))
                        {
                            if (infoEl.TryGetProperty("current_layer", out var clEl) && clEl.ValueKind == JsonValueKind.Number)
                                status.CurrentLayer = clEl.GetInt32();
                            if (infoEl.TryGetProperty("total_layer", out var tlEl) && tlEl.ValueKind == JsonValueKind.Number)
                                status.TotalLayers = tlEl.GetInt32();
                        }
                    }

                    string idleState = "";
                    double idleTime = 0.0;
                    if (st.TryGetProperty("idle_timeout", out var it))
                    {
                        if (it.TryGetProperty("state", out var itStateEl)) idleState = itStateEl.GetString() ?? "";
                        if (it.TryGetProperty("printing_time", out var itTimeEl)) idleTime = itTimeEl.GetDouble();
                    }

                    bool vsdActive = false;
                    double vsdProgress = 0.0;
                    string vsdPath = "";
                    if (st.TryGetProperty("virtual_sdcard", out var vsd))
                    {
                        if (vsd.TryGetProperty("is_active", out var vsdActEl)) vsdActive = vsdActEl.GetBoolean();
                        if (vsd.TryGetProperty("progress", out var vsdProgEl)) vsdProgress = vsdProgEl.GetDouble();
                        if (vsd.TryGetProperty("file_path", out var vsdPathEl)) vsdPath = vsdPathEl.GetString() ?? "";

                        if (status.CurrentLayer == 0 && vsd.TryGetProperty("layer", out var layerEl) && layerEl.ValueKind == JsonValueKind.Number)
                            status.CurrentLayer = layerEl.GetInt32();
                        if (status.TotalLayers == 0 && vsd.TryGetProperty("layer_count", out var lCntEl) && lCntEl.ValueKind == JsonValueKind.Number)
                            status.TotalLayers = lCntEl.GetInt32();
                    }

                    double dispProgress = 0.0;
                    if (st.TryGetProperty("display_status", out var ds))
                    {
                        if (ds.TryGetProperty("progress", out var dsProgEl)) dispProgress = dsProgEl.GetDouble();
                    }

                    double progress = Math.Max(vsdProgress, dispProgress);
                    status.ProgressPct = Math.Round(progress * 100.0, 1);

                    if (string.IsNullOrEmpty(status.Filename) && !string.IsNullOrEmpty(vsdPath))
                    {
                        status.Filename = Path.GetFileName(vsdPath);
                    }
                    status.ProjectName = CleanProjectName(status.Filename);

                    if (st.TryGetProperty("extruder", out var ext))
                    {
                        if (ext.TryGetProperty("temperature", out var tEl)) status.NozzleTemp = Math.Round(tEl.GetDouble(), 1);
                        if (ext.TryGetProperty("target", out var tgtEl)) status.NozzleTarget = Math.Round(tgtEl.GetDouble(), 1);
                    }

                    if (st.TryGetProperty("heater_bed", out var bed))
                    {
                        if (bed.TryGetProperty("temperature", out var tEl)) status.BedTemp = Math.Round(tEl.GetDouble(), 1);
                        if (bed.TryGetProperty("target", out var tgtEl)) status.BedTarget = Math.Round(tgtEl.GetDouble(), 1);
                    }

                    // Determine print state
                    rawState = rawState.ToLowerInvariant();
                    bool isIdlePrinting = idleState.Equals("Printing", StringComparison.OrdinalIgnoreCase);
                    bool isHeating = status.NozzleTarget > 0 || status.BedTarget > 0;

                    if (rawState == "printing")
                    {
                        status.IsPrinting = true;
                        status.State = "printing";
                        status.StateLabel = "In Stampa";
                    }
                    else if (rawState == "paused")
                    {
                        status.IsPrinting = true;
                        status.State = "paused";
                        status.StateLabel = "In Pausa";
                    }
                    else if (rawState == "standby" && (isIdlePrinting || vsdActive || (isHeating && !string.IsNullOrEmpty(status.Filename))))
                    {
                        status.IsPrinting = true;
                        status.State = "preparing";
                        status.StateLabel = "In Preparazione / Riscaldamento";
                        if (status.PrintDurationSec <= 0 && idleTime > 0)
                        {
                            status.PrintDurationSec = idleTime;
                            status.PrintDurationStr = FormatDuration(idleTime);
                        }
                    }
                    else
                    {
                        status.IsPrinting = false;
                        status.State = rawState == "complete" ? "completed" : (rawState == "cancelled" ? "cancelled" : (rawState == "error" ? "error" : "ready"));
                        status.StateLabel = status.State == "ready" ? "Pronta" : (status.State == "completed" ? "Completata" : (status.State == "cancelled" ? "Interrotta" : status.State));
                    }

                    // Fetch / apply thumbnail and estimated duration for active print
                    if (!string.IsNullOrEmpty(status.Filename))
                    {
                        if (thumbCache != null && thumbCache.TryGetValue(status.Filename, out var t))
                        {
                            status.Thumbnail = t;
                        }

                        if (ActiveFileMetaCache.TryGetValue(status.Filename, out var m))
                        {
                            if (string.IsNullOrEmpty(status.Thumbnail)) status.Thumbnail = m.Thumbnail;
                            status.TimeEstStr = m.TimeEst ?? "N/D";
                            if (status.TotalLayers == 0 && m.TotalLayers.HasValue) status.TotalLayers = m.TotalLayers.Value;

                            status.IsMulticolor = m.FilamentColors.Count > 1 || m.FilamentGramsList.Count > 1;
                            status.FilamentChanges = m.TotalFilamentChanges;

                            var totalPlannedG = m.TotalFilamentGrams ?? (m.FilamentGramsList.Count > 0 ? m.FilamentGramsList.Sum() : (m.FilamentGEst ?? 0.0));
                            var filList = new List<FilamentColorInfo>();
                            int slotCount = Math.Max(m.FilamentGramsList.Count, Math.Max(m.FilamentColors.Count, 1));
                            for (int i = 0; i < slotCount; i++)
                            {
                                double g = (i < m.FilamentGramsList.Count) ? m.FilamentGramsList[i] : (slotCount == 1 ? totalPlannedG : 0.0);
                                if (slotCount > 1 && g <= 0.05) continue;
                                double meters = (i < m.FilamentMetersList.Count) ? m.FilamentMetersList[i] : 0.0;
                                double cost = (i < m.FilamentCostsList.Count) ? m.FilamentCostsList[i] : 0.0;
                                string hex = (i < m.FilamentColors.Count) ? m.FilamentColors[i] : (i == 0 ? "#10B981" : "#3B82F6");
                                string type = (i < m.FilamentTypes.Count) ? m.FilamentTypes[i] : (m.FilamentType ?? "PLA");
                                string name = (i < m.FilamentNames.Count) ? m.FilamentNames[i] : (m.FilamentName ?? "Standard");
                                double pct = totalPlannedG > 0 ? (g / totalPlannedG) * 100.0 : 100.0;

                                filList.Add(new FilamentColorInfo
                                {
                                    Index = i,
                                    ColorHex = hex,
                                    Name = name,
                                    Type = type,
                                    UsedG = Math.Round(g, 2),
                                    UsedM = Math.Round(meters, 2),
                                    CostEur = Math.Round(cost, 2),
                                    Pct = Math.Round(pct, 1)
                                });
                            }
                            status.Filaments = filList;

                            var cache = LoadFeatureBreakdownCache();
                            var key = Path.GetFileName(status.Filename);
                            if (cache.TryGetValue(key, out var cachedEntry))
                            {
                                status.Breakdown = BuildBreakdownFromPercentages(
                                    cachedEntry.ModelPct, cachedEntry.SupportPct, cachedEntry.PrimeTowerPct, cachedEntry.BrimPct,
                                    totalPlannedG, m.TotalFilamentCost ?? 0.0);
                            }
                            else if (m.EnableSupport == "0" && m.EnablePrimeTower != "1" && (m.BrimType == "no_brim" || m.BrimWidth == "0" || string.IsNullOrEmpty(m.BrimWidth)))
                            {
                                status.Breakdown = BuildBreakdownFromPercentages(100.0, 0.0, 0.0, 0.0, totalPlannedG, m.TotalFilamentCost ?? 0.0);
                            }
                        }
                        else if (status.IsPrinting)
                        {
                            // Asynchronously extract metadata once for active file
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var meta = await ExtractFileMetadataAsync(ip, port, status.Filename, 0);
                                    ActiveFileMetaCache[status.Filename] = meta;
                                }
                                catch { }
                            });
                        }
                    }
                }
            }
            catch
            {
                status.IsConnected = false;
                status.State = "offline";
                status.StateLabel = "Disconnessa";
            }
            return status;
        }

        public static List<PrintJob> LoadCachedHistory()
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var json = File.ReadAllText(CacheFile);
                    var jobs = JsonSerializer.Deserialize<List<PrintJob>>(json);
                    if (jobs != null) return jobs;
                }
            }
            catch { }
            return new List<PrintJob>();
        }

        public static void SaveCachedHistory(List<PrintJob> jobs)
        {
            try
            {
                var json = JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CacheFile, json);
            }
            catch { }
        }

        public static async Task<List<PrintJob>> SyncHistoryAsync(string ip, int port, double costPerKg)
        {
            var histUrl = $"http://{ip}:{port}/server/history/list?limit=250";
            var histJson = await Http.GetStringAsync(histUrl);
            using var histDoc = JsonDocument.Parse(histJson);
            var jobsEl = histDoc.RootElement.GetProperty("result").GetProperty("jobs");

            var filesUrl = $"http://{ip}:{port}/server/files/list?root=gcodes";
            var filesJson = await Http.GetStringAsync(filesUrl);
            using var filesDoc = JsonDocument.Parse(filesJson);
            var filesEl = filesDoc.RootElement.GetProperty("result");

            var fileMap = new Dictionary<string, (string path, long size)>(StringComparer.OrdinalIgnoreCase);
            var baseMap = new Dictionary<string, (string path, long size)>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in filesEl.EnumerateArray())
            {
                var p = f.GetProperty("path").GetString() ?? "";
                var sz = f.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0L;
                fileMap[p] = (p, sz);
                var b = Path.GetFileName(p);
                if (!baseMap.ContainsKey(b)) baseMap[b] = (p, sz);
            }

            // Identify unique files to extract metadata for
            var targetFiles = new Dictionary<string, (string path, long size)>();
            foreach (var j in jobsEl.EnumerateArray())
            {
                var fname = j.TryGetProperty("filename", out var fEl) ? fEl.GetString() : null;
                if (string.IsNullOrEmpty(fname) || targetFiles.ContainsKey(fname)) continue;

                if (fileMap.TryGetValue(fname, out var info) || baseMap.TryGetValue(fname, out info))
                {
                    targetFiles[fname] = info;
                }
            }

            // Parallel extraction using Range requests
            var metaCache = new ConcurrentDictionary<string, FileMetadata>();
            var tasks = new List<Task>();
            using var sem = new System.Threading.SemaphoreSlim(8);

            foreach (var kvp in targetFiles)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var meta = await ExtractFileMetadataAsync(ip, port, kvp.Value.path, kvp.Value.size);
                        metaCache[kvp.Key] = meta;
                    }
                    finally
                    {
                        sem.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks);

            var list = new List<PrintJob>();
            foreach (var j in jobsEl.EnumerateArray())
            {
                var fname = j.TryGetProperty("filename", out var fEl) ? fEl.GetString() ?? "Unknown" : "Unknown";
                var status = j.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "unknown" : "unknown";
                var printDur = j.TryGetProperty("print_duration", out var pdEl) ? pdEl.GetDouble() : 0.0;
                var totalDur = j.TryGetProperty("total_duration", out var tdEl) ? tdEl.GetDouble() : 0.0;
                var filMm = j.TryGetProperty("filament_used", out var fuEl) ? fuEl.GetDouble() : 0.0;
                var startTs = j.TryGetProperty("start_time", out var stEl) && stEl.ValueKind == JsonValueKind.Number ? stEl.GetDouble() : 0.0;
                var endTs = j.TryGetProperty("end_time", out var etEl) && etEl.ValueKind == JsonValueKind.Number ? etEl.GetDouble() : 0.0;
                var jobId = j.TryGetProperty("job_id", out var idEl) ? idEl.GetString() ?? "" : "";

                metaCache.TryGetValue(fname, out var m);

                var filM = filMm / 1000.0;
                var density = m?.Density ?? 1.24;
                var calcGrams = (filMm * Math.PI * Math.Pow(1.75 / 2.0, 2) / 1000.0) * density;
                var actualGrams = (status == "completed" && m?.TotalFilamentGrams.HasValue == true)
                    ? m.TotalFilamentGrams.Value
                    : (calcGrams > 0 ? calcGrams : (m?.FilamentGEst ?? 0.0));

                var costEur = (m?.TotalFilamentCost != null && status == "completed")
                    ? m.TotalFilamentCost.Value
                    : ((m?.FilamentCostEst != null && status == "completed")
                        ? m.FilamentCostEst.Value
                        : actualGrams * (costPerKg / 1000.0));

                var filList = new List<FilamentColorInfo>();
                var gramsList = m?.FilamentGramsList ?? new List<double>();
                var metersList = m?.FilamentMetersList ?? new List<double>();
                var costsList = m?.FilamentCostsList ?? new List<double>();
                var colorsList = m?.FilamentColors ?? new List<string>();
                var typesList = m?.FilamentTypes ?? new List<string>();
                var namesList = m?.FilamentNames ?? new List<string>();

                double totalG = actualGrams > 0 ? actualGrams : (m?.TotalFilamentGrams ?? (gramsList.Count > 0 ? gramsList.Sum() : 0.0));

                int slotCount = Math.Max(gramsList.Count, Math.Max(colorsList.Count, 1));
                for (int i = 0; i < slotCount; i++)
                {
                    double g = (i < gramsList.Count) ? gramsList[i] : (slotCount == 1 ? totalG : 0.0);
                    if (slotCount > 1 && g <= 0.05) continue;

                    double meters = (i < metersList.Count) ? metersList[i] : (totalG > 0 ? (g / totalG) * filM : filM);
                    double cost = (i < costsList.Count) ? costsList[i] : g * (costPerKg / 1000.0);
                    string hex = (i < colorsList.Count) ? colorsList[i] : (i == 0 ? "#10B981" : "#3B82F6");
                    string type = (i < typesList.Count) ? typesList[i] : (m?.FilamentType ?? "PLA");
                    string name = (i < namesList.Count) ? namesList[i] : (m?.FilamentName ?? "Standard");
                    double pct = totalG > 0 ? (g / totalG) * 100.0 : 100.0;

                    filList.Add(new FilamentColorInfo
                    {
                        Index = i,
                        ColorHex = hex,
                        Name = name,
                        Type = type,
                        UsedG = Math.Round(g, 2),
                        UsedM = Math.Round(meters, 2),
                        CostEur = Math.Round(cost, 2),
                        Pct = Math.Round(pct, 1)
                    });
                }

                if (filList.Count == 0)
                {
                    filList.Add(new FilamentColorInfo
                    {
                        Index = 0,
                        ColorHex = colorsList.Count > 0 ? colorsList[0] : "#10B981",
                        Name = m?.FilamentName ?? "Standard",
                        Type = m?.FilamentType ?? "PLA",
                        UsedG = Math.Round(totalG, 2),
                        UsedM = Math.Round(filM, 2),
                        CostEur = Math.Round(costEur, 2),
                        Pct = 100.0
                    });
                }

                PrintFeatureBreakdown? itemBreakdown = null;
                if (m?.Breakdown != null)
                {
                    itemBreakdown = BuildBreakdownFromPercentages(
                        m.Breakdown.ModelPct, m.Breakdown.SupportPct, m.Breakdown.PrimeTowerPct, m.Breakdown.BrimPct,
                        actualGrams, costEur);
                }
                else
                {
                    var cache = LoadFeatureBreakdownCache();
                    var key = Path.GetFileName(fname);
                    if (cache.TryGetValue(key, out var cachedEntry))
                    {
                        itemBreakdown = BuildBreakdownFromPercentages(
                            cachedEntry.ModelPct, cachedEntry.SupportPct, cachedEntry.PrimeTowerPct, cachedEntry.BrimPct,
                            actualGrams, costEur);
                    }
                    else if (m?.EnableSupport == "0" && m?.EnablePrimeTower != "1" && (m?.BrimType == "no_brim" || m?.BrimWidth == "0" || string.IsNullOrEmpty(m?.BrimWidth)))
                    {
                        itemBreakdown = BuildBreakdownFromPercentages(100.0, 0.0, 0.0, 0.0, actualGrams, costEur);
                    }
                }

                var startDt = startTs > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)startTs).ToLocalTime() : (DateTimeOffset?)null;
                var endDt = endTs > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)endTs).ToLocalTime() : (DateTimeOffset?)null;

                var estSec = ParseTimeStrToSeconds(m?.TimeEst);
                string deltaStr = "";
                double? deltaPct = null;
                if (status == "completed" && estSec.HasValue && printDur > 10)
                {
                    var diffSec = printDur - estSec.Value;
                    var pct = (diffSec / estSec.Value) * 100.0;
                    deltaPct = pct;
                    deltaStr = pct < 0 ? $"-{Math.Abs(pct):F1}%" : $"+{pct:F1}%";
                }

                var supEnabled = m?.EnableSupport == "1";
                var supType = m?.SupportType ?? "Normale";
                var supLabel = supType.Contains("tree", StringComparison.OrdinalIgnoreCase) ? "Tree" : (supEnabled ? "Standard" : "Nessuno");

                var item = new PrintJob
                {
                    JobId = jobId,
                    Filename = fname,
                    ProjectName = CleanProjectName(fname),
                    Status = status,
                    StatusLabel = status == "completed" ? "Completata" : (status == "cancelled" ? "Interrotta" : status),
                    StartTime = startDt?.ToString("yyyy-MM-dd HH:mm") ?? "N/D",
                    EndTime = endDt?.ToString("yyyy-MM-dd HH:mm") ?? "N/D",
                    Timestamp = startTs,
                    PrintDuration = printDur,
                    PrintDurationStr = FormatDuration(printDur),
                    TotalDuration = totalDur,
                    TotalDurationStr = FormatDuration(totalDur),
                    TimeEstStr = m?.TimeEst ?? "N/D",
                    DeltaStr = deltaStr,
                    DeltaPct = deltaPct,
                    FilamentMm = Math.Round(filMm, 1),
                    FilamentM = Math.Round(filM, 2),
                    FilamentG = Math.Round(actualGrams, 1),
                    FilamentCostEur = Math.Round(costEur, 2),
                    FilamentType = filList.Count > 1 ? string.Join(", ", filList.Select(f => f.Type).Distinct()) : (m?.FilamentType ?? "PLA"),
                    FilamentName = filList.Count > 1 ? $"{filList.Count} Colori ({string.Join(", ", filList.Select(f => f.Name))})" : (m?.FilamentName ?? "Standard"),
                    Filaments = filList,
                    IsMulticolor = filList.Count > 1,
                    FilamentChanges = m?.TotalFilamentChanges ?? 0,
                    Breakdown = itemBreakdown,
                    Thumbnail = m?.Thumbnail,
                    Slicer = m?.Slicer ?? "Creality Print",
                    TotalLayers = m?.TotalLayers?.ToString() ?? "N/D",
                    MaxZ = m?.MaxZ?.ToString("F1") ?? "N/D",
                    NozzleTemp = m?.NozzleTemp ?? "N/D",
                    FirstLayerNozzleTemp = m?.FirstLayerNozzleTemp ?? "N/D",
                    BedTemp = m?.BedTemp ?? "N/D",
                    FirstLayerBedTemp = m?.FirstLayerBedTemp ?? "N/D",
                    PrintProfile = m?.PrintSettingsId ?? "Standard",
                    PrinterProfile = m?.PrinterSettingsId ?? "SparkX i7",
                    NozzleDiameter = m?.NozzleDiameter ?? "0.4",
                    LayerHeight = m?.LayerHeight ?? "N/D",
                    FirstLayerHeight = m?.FirstLayerHeight ?? "N/D",
                    InfillDensity = m?.InfillDensity ?? "N/D",
                    InfillPattern = m?.InfillPattern ?? "N/D",
                    WallLoops = m?.WallLoops ?? "N/D",
                    SupportsEnabled = supEnabled,
                    SupportLabel = supLabel,
                    SupportType = supType,
                    BedType = m?.CurrBedType ?? "PEI Plate",
                    HasIroning = !string.IsNullOrEmpty(m?.IroningType) && m.IroningType != "no ironing" && m.IroningType != "none",
                    IroningType = m?.IroningType ?? "no ironing",
                    BrimType = m?.BrimType ?? "N/D",
                    BrimWidth = m?.BrimWidth ?? "N/D",
                    OuterWallSpeed = m?.OuterWallSpeed ?? "N/D",
                    InnerWallSpeed = m?.InnerWallSpeed ?? "N/D",
                    InfillSpeed = m?.InfillSpeed ?? "N/D",
                    TravelSpeed = m?.TravelSpeed ?? "N/D",
                    FanSpeed = m?.FanMaxSpeed ?? "N/D",
                    SeamPosition = m?.SeamPosition ?? "N/D",
                    FileSizeMb = m?.FileSize > 0 ? (m.FileSize / (1024.0 * 1024.0)).ToString("F2") : "N/D"
                };
                list.Add(item);
            }

            SaveCachedHistory(list);
            return list;
        }

        private static async Task<FileMetadata> ExtractFileMetadataAsync(string ip, int port, string relPath, long knownSize)
        {
            var meta = new FileMetadata { FileSize = knownSize };
            var url = $"http://{ip}:{port}/server/files/gcodes/{Uri.EscapeDataString(relPath)}";

            try
            {
                using var reqHead = new HttpRequestMessage(HttpMethod.Get, url);
                reqHead.Headers.Range = new RangeHeaderValue(0, 46080);
                using var respHead = await Http.SendAsync(reqHead);
                var headStr = await respHead.Content.ReadAsStringAsync();

                if (respHead.Content.Headers.ContentRange?.Length.HasValue == true)
                {
                    meta.FileSize = respHead.Content.Headers.ContentRange.Length.Value;
                }

                // Thumbnail regex
                var thumbMatch = Regex.Matches(headStr, @";\s*thumbnail begin\s+(\d+x\d+)\s+(\d+)\s*\n((?:;[^\n]*\n)+?);\s*thumbnail end");
                if (thumbMatch.Count > 0)
                {
                    Match? best = null;
                    int maxRes = 0;
                    foreach (Match tm in thumbMatch)
                    {
                        var resStr = tm.Groups[1].Value.Split('x')[0];
                        if (int.TryParse(resStr, out var r) && r > maxRes)
                        {
                            maxRes = r;
                            best = tm;
                        }
                    }
                    if (best != null)
                    {
                        var raw = Regex.Replace(best.Groups[3].Value, @"[\s;]", "");
                        meta.Thumbnail = $"data:image/png;base64,{raw}";
                    }
                }

                var sm = Regex.Match(headStr, @";\s*generated by\s+([^\n]+)");
                if (sm.Success) meta.Slicer = sm.Groups[1].Value.Trim();

                var lm = Regex.Match(headStr, @";\s*total layer number:\s*(\d+)");
                if (lm.Success && int.TryParse(lm.Groups[1].Value, out var lCount)) meta.TotalLayers = lCount;

                var zm = Regex.Match(headStr, @";\s*max_z_height:\s*([\d\.]+)");
                if (zm.Success && double.TryParse(zm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var mz)) meta.MaxZ = mz;

                var dm = Regex.Match(headStr, @";\s*filament_density:\s*([\d\.]+)");
                if (dm.Success && double.TryParse(dm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var dens)) meta.Density = dens;
            }
            catch { }

            // Footer range
            if (meta.FileSize > 0)
            {
                try
                {
                    var startByte = Math.Max(0, meta.FileSize - 163840);
                    using var reqTail = new HttpRequestMessage(HttpMethod.Get, url);
                    reqTail.Headers.Range = new RangeHeaderValue(startByte, meta.FileSize);
                    using var respTail = await Http.SendAsync(reqTail);
                    var tailStr = await respTail.Content.ReadAsStringAsync();

                    using var reader = new StringReader(tailStr);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (!line.StartsWith(";") || !line.Contains('=')) continue;

                        var parts = line.TrimStart(';').Split('=', 2);
                        var k = parts[0].Trim();
                        var v = parts[1].Trim().Trim('\"');

                        if (k == "total filament used [g]")
                        {
                            var s = v.Split(',')[0].Trim();
                            if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var g))
                            {
                                meta.TotalFilamentGrams = g;
                                if (meta.FilamentGEst == null || meta.FilamentGEst == 0) meta.FilamentGEst = g;
                            }
                        }
                        else if (k == "filament used [g]")
                        {
                            var items = v.Split(',');
                            var list = new List<double>();
                            foreach (var item in items)
                            {
                                if (double.TryParse(item.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var val))
                                    list.Add(val);
                            }
                            if (list.Count > 0)
                            {
                                meta.FilamentGramsList = list;
                                if (meta.FilamentGEst == null || meta.FilamentGEst == 0) meta.FilamentGEst = list[0];
                            }
                        }
                        else if (k == "filament used [mm]")
                        {
                            var items = v.Split(',');
                            var list = new List<double>();
                            foreach (var item in items)
                            {
                                if (double.TryParse(item.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var val))
                                    list.Add(val / 1000.0);
                            }
                            if (list.Count > 0) meta.FilamentMetersList = list;
                        }
                        else if (k == "total filament cost")
                        {
                            var s = v.Split(',')[0].Trim();
                            if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var c))
                            {
                                meta.TotalFilamentCost = c;
                                if (meta.FilamentCostEst == null || meta.FilamentCostEst == 0) meta.FilamentCostEst = c;
                            }
                        }
                        else if (k == "filament cost")
                        {
                            var items = v.Split(',');
                            var list = new List<double>();
                            foreach (var item in items)
                            {
                                if (double.TryParse(item.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var val))
                                    list.Add(val);
                            }
                            if (list.Count > 0)
                            {
                                meta.FilamentCostsList = list;
                                if (meta.FilamentCostEst == null || meta.FilamentCostEst == 0) meta.FilamentCostEst = list[0];
                            }
                        }
                        else if (k == "total filament change")
                        {
                            if (int.TryParse(v.Trim(), out var ch)) meta.TotalFilamentChanges = ch;
                        }
                        else if (k is "filament_colour" or "default_filament_colour")
                        {
                            if (meta.FilamentColors.Count == 0 || k == "filament_colour")
                            {
                                var cols = v.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                meta.FilamentColors = cols.Select(c => c.Trim().Trim('\"')).Where(c => c.StartsWith("#") || c.Length >= 4).ToList();
                            }
                        }
                        else if (k == "filament_settings_id")
                        {
                            var names = v.Split(';');
                            meta.FilamentNames = names.Select(n => n.Trim().Trim('\"')).Where(n => !string.IsNullOrEmpty(n)).ToList();
                            if (meta.FilamentNames.Count > 0) meta.FilamentName = meta.FilamentNames[0];
                        }
                        else if (k == "filament_type")
                        {
                            var types = v.Split(';');
                            meta.FilamentTypes = types.Select(t => t.Trim().Trim('\"')).Where(t => !string.IsNullOrEmpty(t)).ToList();
                            if (meta.FilamentTypes.Count > 0) meta.FilamentType = meta.FilamentTypes[0];
                        }
                        else if (k == "enable_prime_tower")
                        {
                            meta.EnablePrimeTower = v.Trim();
                        }
                        else if (k.Contains("estimated printing time", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(meta.TimeEst))
                        {
                            meta.TimeEst = v;
                        }
                        else if (k == "print_settings_id") meta.PrintSettingsId = v;
                        else if (k == "printer_settings_id") meta.PrinterSettingsId = v;
                        else if (k == "nozzle_diameter") meta.NozzleDiameter = v.Split(',')[0].Trim();
                        else if (k == "layer_height") meta.LayerHeight = v.Split(',')[0].Trim();
                        else if (k == "first_layer_height") meta.FirstLayerHeight = v.Split(',')[0].Trim();
                        else if (k == "sparse_infill_density") meta.InfillDensity = v;
                        else if (k == "sparse_infill_pattern") meta.InfillPattern = v;
                        else if (k == "wall_loops") meta.WallLoops = v;
                        else if (k == "enable_support") meta.EnableSupport = v;
                        else if (k == "support_type") meta.SupportType = v;
                        else if (k == "curr_bed_type") meta.CurrBedType = v;
                        else if (k == "ironing_type") meta.IroningType = v;
                        else if (k == "brim_type") meta.BrimType = v;
                        else if (k == "brim_width") meta.BrimWidth = v;
                        else if (k == "outer_wall_speed") meta.OuterWallSpeed = v;
                        else if (k == "inner_wall_speed") meta.InnerWallSpeed = v;
                        else if (k == "sparse_infill_speed") meta.InfillSpeed = v;
                        else if (k == "travel_speed") meta.TravelSpeed = v;
                        else if (k == "fan_max_speed") meta.FanMaxSpeed = v;
                        else if (k == "seam_position") meta.SeamPosition = v;
                        else if (k == "nozzle_temperature") meta.NozzleTemp = v.Split(',')[0].Trim();
                        else if (k is "first_layer_temperature" or "nozzle_temperature_initial_layer") meta.FirstLayerNozzleTemp = v.Split(',')[0].Trim();
                        else if (k is "bed_temperature" or "textured_plate_temp") meta.BedTemp = v.Split(',')[0].Trim();
                        else if (k is "first_layer_bed_temperature" or "textured_plate_temp_initial_layer") meta.FirstLayerBedTemp = v.Split(',')[0].Trim();
                    }
                }
                catch { }
            }

            return meta;
        }

        public static async Task<bool> StartReprintAsync(string ip, int port, string filename)
        {
            try
            {
                var url = $"http://{ip}:{port}/printer/print/start";
                var payload = JsonSerializer.Serialize(new { filename });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var res = await Http.PostAsync(url, content);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public static async Task DownloadGcodeAsync(string ip, int port, string filename, string targetLocalPath)
        {
            var url = $"http://{ip}:{port}/server/files/gcodes/{Uri.EscapeDataString(filename)}";
            using var stream = await Http.GetStreamAsync(url);
            using var fileStream = File.Create(targetLocalPath);
            await stream.CopyToAsync(fileStream);
        }

        public static Dictionary<string, FeatureBreakdownPercentCache> LoadFeatureBreakdownCache()
        {
            try
            {
                if (File.Exists(BreakdownCacheFile))
                {
                    var json = File.ReadAllText(BreakdownCacheFile);
                    var res = JsonSerializer.Deserialize<Dictionary<string, FeatureBreakdownPercentCache>>(json);
                    if (res != null) return res;
                }
            }
            catch { }
            return new Dictionary<string, FeatureBreakdownPercentCache>(StringComparer.OrdinalIgnoreCase);
        }

        public static void SaveFeatureBreakdownCache(Dictionary<string, FeatureBreakdownPercentCache> cache)
        {
            try
            {
                var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(BreakdownCacheFile, json);
            }
            catch { }
        }

        public static PrintFeatureBreakdown BuildBreakdownFromPercentages(
            double modelPct, double supportPct, double primeTowerPct, double brimPct,
            double totalG, double totalCost)
        {
            var modelG = Math.Round(totalG * (modelPct / 100.0), 1);
            var supportG = Math.Round(totalG * (supportPct / 100.0), 1);
            var primeTowerG = Math.Round(totalG * (primeTowerPct / 100.0), 1);
            var brimG = Math.Round(totalG * (brimPct / 100.0), 1);
            var wasteTotalG = Math.Round(supportG + primeTowerG + brimG, 1);
            var wasteTotalPct = Math.Round(supportPct + primeTowerPct + brimPct, 1);

            var modelCost = Math.Round(totalCost * (modelPct / 100.0), 2);
            var supportCost = Math.Round(totalCost * (supportPct / 100.0), 2);
            var primeTowerCost = Math.Round(totalCost * (primeTowerPct / 100.0), 2);
            var brimCost = Math.Round(totalCost * (brimPct / 100.0), 2);
            var wasteTotalCost = Math.Round(supportCost + primeTowerCost + brimCost, 2);

            return new PrintFeatureBreakdown
            {
                ModelG = modelG,
                ModelPct = Math.Round(modelPct, 1),
                ModelCostEur = modelCost,
                SupportG = supportG,
                SupportPct = Math.Round(supportPct, 1),
                SupportCostEur = supportCost,
                PrimeTowerG = primeTowerG,
                PrimeTowerPct = Math.Round(primeTowerPct, 1),
                PrimeTowerCostEur = primeTowerCost,
                BrimG = brimG,
                BrimPct = Math.Round(brimPct, 1),
                BrimCostEur = brimCost,
                WasteTotalG = wasteTotalG,
                WasteTotalPct = wasteTotalPct,
                WasteTotalCostEur = wasteTotalCost,
                HasBreakdown = true
            };
        }

        public static async Task<PrintFeatureBreakdown?> ComputeFeatureBreakdownAsync(
            string ip, int port, string relPath, double totalG, double totalCostEur,
            string? enableSupport = null, string? enablePrimeTower = null,
            string? brimType = null, string? brimWidth = null)
        {
            var key = Path.GetFileName(relPath);
            var cache = LoadFeatureBreakdownCache();
            if (cache.TryGetValue(key, out var cachedEntry))
            {
                return BuildBreakdownFromPercentages(cachedEntry.ModelPct, cachedEntry.SupportPct, cachedEntry.PrimeTowerPct, cachedEntry.BrimPct, totalG, totalCostEur);
            }

            // Fast-path: When there is no support, no prime tower, and no brim, 100% of filament is model
            if (enableSupport == "0" && enablePrimeTower != "1" &&
                (brimType == "no_brim" || brimWidth == "0" || string.IsNullOrEmpty(brimWidth)))
            {
                var fastEntry = new FeatureBreakdownPercentCache { ModelPct = 100.0, SupportPct = 0.0, PrimeTowerPct = 0.0, BrimPct = 0.0 };
                cache[key] = fastEntry;
                SaveFeatureBreakdownCache(cache);
                return BuildBreakdownFromPercentages(100.0, 0.0, 0.0, 0.0, totalG, totalCostEur);
            }

            try
            {
                var url = $"http://{ip}:{port}/server/files/gcodes/{Uri.EscapeDataString(relPath)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return null;

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 65536);

                string currentFeature = "model";
                double mmModel = 0.0;
                double mmTower = 0.0;
                double mmSupport = 0.0;
                double mmBrim = 0.0;

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    if (trimmed.StartsWith(";TYPE:", StringComparison.OrdinalIgnoreCase))
                    {
                        var feature = trimmed.Substring(6).Trim().ToLowerInvariant();
                        if (feature.Contains("prime tower") || feature.Contains("wipe tower"))
                            currentFeature = "tower";
                        else if (feature.Contains("support"))
                            currentFeature = "support";
                        else if (feature.Contains("brim") || feature.Contains("skirt"))
                            currentFeature = "brim";
                        else
                            currentFeature = "model";
                        continue;
                    }

                    if (trimmed.StartsWith(";"))
                    {
                        if (trimmed.StartsWith("; filament used [", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("; total filament used", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("G1", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("G0", StringComparison.OrdinalIgnoreCase))
                    {
                        int eIdx = trimmed.IndexOf(" E", StringComparison.OrdinalIgnoreCase);
                        if (eIdx < 0 && trimmed.StartsWith("G1 E", StringComparison.OrdinalIgnoreCase)) eIdx = 2;

                        if (eIdx >= 0)
                        {
                            var sub = trimmed.Substring(eIdx + 1);
                            var parts = sub.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0 && (parts[0].StartsWith("E", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("e")))
                            {
                                var numStr = parts[0].Substring(1);
                                if (double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var eVal) && eVal > 0 && eVal < 200.0)
                                {
                                    switch (currentFeature)
                                    {
                                        case "tower": mmTower += eVal; break;
                                        case "support": mmSupport += eVal; break;
                                        case "brim": mmBrim += eVal; break;
                                        default: mmModel += eVal; break;
                                    }
                                }
                            }
                        }
                    }
                }

                double totalMm = mmModel + mmTower + mmSupport + mmBrim;
                if (totalMm <= 0) return null;

                var modelPct = Math.Round((mmModel / totalMm) * 100.0, 1);
                var towerPct = Math.Round((mmTower / totalMm) * 100.0, 1);
                var supportPct = Math.Round((mmSupport / totalMm) * 100.0, 1);
                var brimPct = Math.Round((mmBrim / totalMm) * 100.0, 1);

                var entry = new FeatureBreakdownPercentCache
                {
                    ModelPct = modelPct,
                    SupportPct = supportPct,
                    PrimeTowerPct = towerPct,
                    BrimPct = brimPct
                };
                cache[key] = entry;
                SaveFeatureBreakdownCache(cache);

                return BuildBreakdownFromPercentages(modelPct, supportPct, towerPct, brimPct, totalG, totalCostEur);
            }
            catch
            {
                return null;
            }
        }

        private static string CleanProjectName(string filename)
        {
            var name = Path.GetFileName(filename);
            name = Regex.Replace(name, @"_plate_\d+\.gcode$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\.gcode(?:\.3mf)?$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"_(?:PLA|PETG|ABS|TPU|ASA)(?:_[0-9hms]+)?$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\.stl$", "", RegexOptions.IgnoreCase);
            name = name.Replace('_', ' ').Replace('+', ' ').Trim();
            return string.IsNullOrEmpty(name) ? Path.GetFileName(filename) : name;
        }

        private static int? ParseTimeStrToSeconds(string? timeStr)
        {
            if (string.IsNullOrEmpty(timeStr) || timeStr == "N/D") return null;
            int total = 0;
            var hm = Regex.Match(timeStr, @"(\d+)\s*h");
            var mm = Regex.Match(timeStr, @"(\d+)\s*m");
            var sm = Regex.Match(timeStr, @"(\d+)\s*s");
            if (hm.Success) total += int.Parse(hm.Groups[1].Value) * 3600;
            if (mm.Success) total += int.Parse(mm.Groups[1].Value) * 60;
            if (sm.Success) total += int.Parse(sm.Groups[1].Value);
            return total > 0 ? total : null;
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "0s";
            var sec = (int)seconds;
            var hours = sec / 3600;
            var minutes = (sec % 3600) / 60;
            var remSec = sec % 60;
            var list = new List<string>();
            if (hours > 0) list.Add($"{hours}h");
            if (minutes > 0 || hours > 0) list.Add($"{minutes}m");
            if (hours == 0 && minutes < 5) list.Add($"{remSec}s");
            return list.Count > 0 ? string.Join(" ", list) : "0s";
        }

        private class FileMetadata
        {
            public long FileSize { get; set; }
            public string? Thumbnail { get; set; }
            public string? Slicer { get; set; }
            public int? TotalLayers { get; set; }
            public double? MaxZ { get; set; }
            public double Density { get; set; } = 1.24;
            public double? FilamentGEst { get; set; }
            public double? FilamentCostEst { get; set; }
            public string? TimeEst { get; set; }
            public string? FilamentType { get; set; }
            public string? FilamentName { get; set; }
            public List<string> FilamentColors { get; set; } = new();
            public List<string> FilamentTypes { get; set; } = new();
            public List<string> FilamentNames { get; set; } = new();
            public List<double> FilamentGramsList { get; set; } = new();
            public List<double> FilamentMetersList { get; set; } = new();
            public List<double> FilamentCostsList { get; set; } = new();
            public double? TotalFilamentGrams { get; set; }
            public double? TotalFilamentCost { get; set; }
            public int TotalFilamentChanges { get; set; }
            public string? EnablePrimeTower { get; set; }
            public PrintFeatureBreakdown? Breakdown { get; set; }
            public string? PrintSettingsId { get; set; }
            public string? PrinterSettingsId { get; set; }
            public string? NozzleDiameter { get; set; }
            public string? LayerHeight { get; set; }
            public string? FirstLayerHeight { get; set; }
            public string? InfillDensity { get; set; }
            public string? InfillPattern { get; set; }
            public string? WallLoops { get; set; }
            public string? EnableSupport { get; set; }
            public string? SupportType { get; set; }
            public string? CurrBedType { get; set; }
            public string? IroningType { get; set; }
            public string? BrimType { get; set; }
            public string? BrimWidth { get; set; }
            public string? OuterWallSpeed { get; set; }
            public string? InnerWallSpeed { get; set; }
            public string? InfillSpeed { get; set; }
            public string? TravelSpeed { get; set; }
            public string? FanMaxSpeed { get; set; }
            public string? SeamPosition { get; set; }
            public string? NozzleTemp { get; set; }
            public string? FirstLayerNozzleTemp { get; set; }
            public string? BedTemp { get; set; }
            public string? FirstLayerBedTemp { get; set; }
        }
    }
}

