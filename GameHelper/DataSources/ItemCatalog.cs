namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using GameHelper.Settings;
    using LibBundle3;
    using LibBundle3.Records;
    using LibBundledGGPK3;
    using Newtonsoft.Json;

    public sealed class CatalogItem
    {
        public string Path { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string English { get; set; } = string.Empty;
        public string ZhCn { get; set; } = string.Empty;
        public string ZhTw { get; set; } = string.Empty;
        public string Art { get; set; } = string.Empty;
    }

    public sealed class CatalogMod
    {
        public string Id { get; set; } = string.Empty;
        public string English { get; set; } = string.Empty;
        public string ZhCn { get; set; } = string.Empty;
        public string ZhTw { get; set; } = string.Empty;
    }

    public sealed class CatalogArea
    {
        public string Id { get; set; } = string.Empty;
        public string English { get; set; } = string.Empty;
        public string ZhCn { get; set; } = string.Empty;
        public string ZhTw { get; set; } = string.Empty;
    }

    public static class ItemCatalog
    {
        private static readonly object Gate = new();
        private static readonly string CachePath = Path.Combine("configs", "item_catalog.json");

        private static Dictionary<string, CatalogItem> byInternal = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> toEnglish = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, CatalogMod> byMod = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, CatalogArea> byArea = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime extractedUtc;
        private static DateTime namesUtc;
        private static DateTime ggpkWriteUtc;
        private static string ggpkPath = string.Empty;
        private static string lastError = string.Empty;
        private static bool loaded;
        private static bool extracting;
        private static bool fetchingNames;
        private static float progress;
        private static int progressDone;
        private static int progressTotal;
        private static string progressStage = string.Empty;

        public static bool IsExtracting
        {
            get { lock (Gate) return extracting; }
        }

        public static bool IsFetchingNames
        {
            get { lock (Gate) return fetchingNames; }
        }

        public static string LastError
        {
            get { lock (Gate) return lastError; }
        }

        public static float Progress
        {
            get { lock (Gate) return progress; }
        }

        public static int ProgressDone
        {
            get { lock (Gate) return progressDone; }
        }

        public static int ProgressTotal
        {
            get { lock (Gate) return progressTotal; }
        }

        public static string ProgressStage
        {
            get { lock (Gate) return progressStage; }
        }

        public static DateTime ExtractedUtc
        {
            get { lock (Gate) return extractedUtc; }
        }

        public static DateTime NamesUtc
        {
            get { lock (Gate) return namesUtc; }
        }

        public static DateTime GgpkWriteUtc
        {
            get { lock (Gate) return ggpkWriteUtc; }
        }

        public static int ItemCount
        {
            get { lock (Gate) return byInternal.Count; }
        }

        public static int ModCount
        {
            get { lock (Gate) return byMod.Count; }
        }

        public static int NamedCount
        {
            get
            {
                lock (Gate)
                {
                    var n = 0;
                    foreach (var item in byInternal.Values)
                    {
                        if (!string.IsNullOrEmpty(item.ZhCn) || !string.IsNullOrEmpty(item.ZhTw))
                        {
                            n++;
                        }
                    }

                    return n;
                }
            }
        }

        public static bool GgpkIsNewerThanCatalog
        {
            get
            {
                lock (Gate)
                {
                    return ggpkWriteUtc > DateTime.MinValue &&
                           extractedUtc > DateTime.MinValue &&
                           ggpkWriteUtc > extractedUtc.AddMinutes(1);
                }
            }
        }

        public static void Touch()
        {
            lock (Gate)
            {
                if (loaded)
                {
                    return;
                }

                loaded = true;
            }

            TryLoadFromDisk();
        }

        public static void ExtractGgpk()
        {
            lock (Gate)
            {
                if (extracting || fetchingNames)
                {
                    return;
                }

                extracting = true;
                lastError = string.Empty;
                progress = 0f;
                progressDone = 0;
                progressTotal = 6;
                progressStage = "open";
            }

            new Thread(() =>
            {
                try
                {
                    ExtractGgpkCore();
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    lock (Gate)
                    {
                        extracting = false;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ItemCatalog.ExtractGgpk",
            }.Start();
        }

        public static void RefreshNames()
        {
            lock (Gate)
            {
                if (extracting || fetchingNames)
                {
                    return;
                }

                fetchingNames = true;
                lastError = string.Empty;
                progress = 0f;
                progressDone = 0;
                progressTotal = Poe2dbNames.PageCount + Poe2dbMods.PageCount + Poe2dbMaps.PageCount;
                progressStage = "names";
            }

            _ = Task.Run(RefreshNamesCore);
        }

        public static bool TryGet(string internalName, out CatalogItem? item)
        {
            Touch();
            lock (Gate)
            {
                return byInternal.TryGetValue(internalName ?? string.Empty, out item);
            }
        }

        public static string ResolveEnglish(string name)
        {
            Touch();
            if (string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            lock (Gate)
            {
                return toEnglish.TryGetValue(name.Trim(), out var en) ? en : name;
            }
        }

        public static IEnumerable<CatalogItem> ItemsWherePathContains(string fragment)
        {
            Touch();
            lock (Gate)
            {
                var list = new List<CatalogItem>();
                foreach (var item in byInternal.Values)
                {
                    if (item.Path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(item);
                    }
                }

                return list;
            }
        }

        public static List<CatalogMod> SnapshotMods()
        {
            Touch();
            lock (Gate)
            {
                return new List<CatalogMod>(byMod.Values);
            }
        }

        public static List<CatalogArea> SnapshotAreas()
        {
            Touch();
            lock (Gate)
            {
                return new List<CatalogArea>(byArea.Values);
            }
        }

        public static bool TryGetArea(string id, out CatalogArea? area)
        {
            Touch();
            lock (Gate)
            {
                return byArea.TryGetValue(id ?? string.Empty, out area);
            }
        }

        private static void ExtractGgpkCore()
        {
            try
            {
                var path = (Core.GHSettings.ContentGgpkPath ?? string.Empty).Trim();
                if (path.Length == 0 || !File.Exists(path))
                {
                    throw new FileNotFoundException("Select Content.ggpk or _.index.bin first", path);
                }

                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "oo2core.dll")))
                {
                    throw new FileNotFoundException("oo2core.dll missing next to GameHelper.exe");
                }

                SetProgress(0, 6, "open");
                byte[] itemsDat;
                byte[] modsDat;
                byte[] areasDat;
                byte[] artDat;
                using (OpenIndex(path, out var index))
                {
                    SetProgress(1, 6, "read");
                    itemsDat = ReadDat(index, "Data/Balance/BaseItemTypes.datc64");
                    SetProgress(2, 6, "read");
                    modsDat = ReadDat(index, "Data/Balance/Mods.datc64");
                    SetProgress(3, 6, "read");
                    areasDat = ReadDat(index, "Data/Balance/WorldAreas.datc64");
                    SetProgress(4, 6, "read");
                    artDat = ReadDat(index, "Data/Balance/ItemVisualIdentity.datc64");
                }

                SetProgress(5, 6, "parse");
                var items = Datc64Strings.ParseBaseItems(itemsDat);
                if (items.Count == 0)
                {
                    throw new InvalidOperationException("BaseItemTypes had no Metadata/Items paths");
                }

                Datc64Strings.ApplyArt(items, itemsDat, artDat);

                Dictionary<string, CatalogItem> previousItems;
                Dictionary<string, CatalogMod> previousMods;
                Dictionary<string, CatalogArea> previousAreas;
                DateTime previousNamesUtc;
                lock (Gate)
                {
                    previousItems = byInternal;
                    previousMods = byMod;
                    previousAreas = byArea;
                    previousNamesUtc = namesUtc;
                }

                foreach (var item in items)
                {
                    if (!previousItems.TryGetValue(item.InternalName, out var old))
                    {
                        continue;
                    }

                    if (!string.Equals(old.English, item.English, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    item.ZhCn = old.ZhCn;
                    item.ZhTw = old.ZhTw;
                }

                var mods = new List<CatalogMod>();
                foreach (var family in Datc64Strings.ParseModFamilies(modsDat))
                {
                    var mod = new CatalogMod { Id = family };
                    if (previousMods.TryGetValue(family, out var oldMod))
                    {
                        mod.English = oldMod.English;
                        mod.ZhCn = oldMod.ZhCn;
                        mod.ZhTw = oldMod.ZhTw;
                    }

                    mods.Add(mod);
                }

                var areas = Datc64Strings.ParseWorldAreas(areasDat);
                foreach (var area in areas)
                {
                    if (!previousAreas.TryGetValue(area.Id, out var oldArea))
                    {
                        continue;
                    }

                    if (!string.Equals(oldArea.English, area.English, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    area.ZhCn = oldArea.ZhCn;
                    area.ZhTw = oldArea.ZhTw;
                }

                SetProgress(6, 6, "save");
                SaveAndApply(new Snapshot
                {
                    ExtractedUtc = DateTime.UtcNow,
                    NamesUtc = previousNamesUtc,
                    GgpkWriteUtc = File.GetLastWriteTimeUtc(path),
                    GgpkPath = path,
                    Items = items,
                    Mods = mods,
                    Areas = areas,
                });
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                lock (Gate)
                {
                    extracting = false;
                }
            }
        }

        private static void RefreshNamesCore()
        {
            try
            {
                List<CatalogItem> items;
                List<CatalogMod> mods;
                List<CatalogArea> areas;
                DateTime extracted;
                DateTime ggpkWrite;
                string path;
                lock (Gate)
                {
                    if (byInternal.Count == 0)
                    {
                        throw new InvalidOperationException("Extract GGPK first");
                    }

                    items = new List<CatalogItem>(byInternal.Values);
                    mods = new List<CatalogMod>(byMod.Values);
                    areas = new List<CatalogArea>(byArea.Values);
                    extracted = extractedUtc;
                    ggpkWrite = ggpkWriteUtc;
                    path = ggpkPath;
                }

                SetProgress(0, Poe2dbNames.PageCount + Poe2dbMods.PageCount + Poe2dbMaps.PageCount, "names");
                var names = Poe2dbNames.FetchEnglishToLocalAsync(TickProgress).GetAwaiter().GetResult();
                foreach (var item in items)
                {
                    if (item.English.Length > 0 && names.TryGetValue(item.English, out var loc))
                    {
                        item.ZhCn = loc.ZhCn;
                        item.ZhTw = loc.ZhTw;
                    }
                }

                var modNames = Poe2dbMods.FetchAsync(TickProgress).GetAwaiter().GetResult();
                foreach (var mod in mods)
                {
                    if (!TryModText(modNames, mod.Id, out var text))
                    {
                        continue;
                    }

                    mod.English = text.En;
                    mod.ZhCn = text.ZhCn;
                    mod.ZhTw = text.ZhTw;
                }

                var mapNames = Poe2dbMaps.FetchAsync(areas, TickProgress).GetAwaiter().GetResult();
                foreach (var area in areas)
                {
                    if (!mapNames.TryGetValue(area.Id, out var loc))
                    {
                        continue;
                    }

                    area.ZhCn = loc.ZhCn;
                    area.ZhTw = loc.ZhTw;
                }

                SaveAndApply(new Snapshot
                {
                    ExtractedUtc = extracted,
                    NamesUtc = DateTime.UtcNow,
                    GgpkWriteUtc = ggpkWrite,
                    GgpkPath = path,
                    Items = items,
                    Mods = mods,
                    Areas = areas,
                });
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                lock (Gate)
                {
                    fetchingNames = false;
                }
            }
        }

        private static void SaveAndApply(Snapshot snapshot)
        {
            Directory.CreateDirectory("configs");
            File.WriteAllText(CachePath, JsonConvert.SerializeObject(snapshot));
            Apply(snapshot);
        }

        private static void SetProgress(int done, int total, string stage)
        {
            lock (Gate)
            {
                progressDone = done;
                progressTotal = total;
                progressStage = stage;
                progress = total <= 0 ? 0f : done / (float)total;
            }
        }

        private static void TickProgress()
        {
            lock (Gate)
            {
                progressDone++;
                progress = progressTotal <= 0 ? 0f : progressDone / (float)progressTotal;
            }
        }

        private static void Fail(Exception ex)
        {
            lock (Gate)
            {
                lastError = MissingOo2Core(ex) ? "oo2core" : ex.Message;
            }

            Console.WriteLine($"[ItemCatalog] {ex}");
        }

        private static bool MissingOo2Core(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    return true;
                }

                if (e is FileNotFoundException &&
                    (e.Message.Contains("oo2core", StringComparison.OrdinalIgnoreCase) ||
                     ((FileNotFoundException)e).FileName?.Contains("oo2core", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return true;
                }
            }

            return false;
        }

        private static IDisposable OpenIndex(string path, out LibBundle3.Index index)
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1 << 16,
                FileOptions.RandomAccess);
            try
            {
                if (path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase))
                {
                    var ggpk = new BundledGGPK(stream, leaveOpen: false, parsePathsInIndex: false);
                    index = ggpk.Index;
                    return ggpk;
                }

                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                var idx = new LibBundle3.Index(stream, leaveOpen: false, parsePaths: false, new SharedBundleFactory(dir));
                index = idx;
                return idx;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static byte[] ReadDat(LibBundle3.Index index, string datPath)
        {
            if (!index.TryGetFile(datPath, out var file) || file is null)
            {
                throw new FileNotFoundException(datPath);
            }

            return file.Read().ToArray();
        }

        private sealed class SharedBundleFactory : IBundleFactory
        {
            private readonly string baseDir;

            public SharedBundleFactory(string baseDirectory)
            {
                baseDir = Path.GetFullPath(baseDirectory);
                if (!baseDir.EndsWith(Path.DirectorySeparatorChar))
                {
                    baseDir += Path.DirectorySeparatorChar;
                }
            }

            public Bundle GetBundle(BundleRecord record)
            {
                var stream = new FileStream(
                    baseDir + record.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1 << 16,
                    FileOptions.RandomAccess);
                return new Bundle(stream, leaveOpen: false, record);
            }

            public Stream CreateBundle(string bundlePath) =>
                throw new NotSupportedException();

            public bool DeleteBundle(string bundlePath) => false;
        }

        private static void TryLoadFromDisk()
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            try
            {
                var snapshot = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(CachePath));
                if (snapshot?.Items == null)
                {
                    return;
                }

                Apply(snapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemCatalog] load: {ex.Message}");
            }
        }

        private static void Apply(Snapshot snapshot)
        {
            var next = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in snapshot.Items)
            {
                if (string.IsNullOrWhiteSpace(item.InternalName))
                {
                    continue;
                }

                next[item.InternalName] = item;
                IndexName(names, item.English, item.English);
                IndexName(names, item.ZhCn, item.English);
                IndexName(names, item.ZhTw, item.English);
            }

            lock (Gate)
            {
                byInternal = next;
                toEnglish = names;
                var nextMods = new Dictionary<string, CatalogMod>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in snapshot.Mods ?? new List<CatalogMod>())
                {
                    if (!string.IsNullOrWhiteSpace(mod.Id))
                    {
                        nextMods[mod.Id] = mod;
                    }
                }

                byMod = nextMods;
                var nextAreas = new Dictionary<string, CatalogArea>(StringComparer.OrdinalIgnoreCase);
                foreach (var area in snapshot.Areas ?? new List<CatalogArea>())
                {
                    if (!string.IsNullOrWhiteSpace(area.Id))
                    {
                        nextAreas[area.Id] = area;
                    }
                }

                byArea = nextAreas;
                lastError = string.Empty;
                extractedUtc = snapshot.ExtractedUtc;
                namesUtc = snapshot.NamesUtc;
                ggpkWriteUtc = snapshot.GgpkWriteUtc;
                ggpkPath = snapshot.GgpkPath ?? string.Empty;
                loaded = true;
            }
        }

        private static bool TryModText(
            Dictionary<string, (string En, string ZhCn, string ZhTw)> names,
            string id,
            out (string En, string ZhCn, string ZhTw) text)
        {
            if (names.TryGetValue(id, out text))
            {
                return true;
            }

            var stripped = Poe2dbMods.StripPrefix(id);
            if (names.TryGetValue(stripped, out text) ||
                names.TryGetValue("Map" + stripped, out text) ||
                names.TryGetValue("Tower" + stripped, out text))
            {
                return true;
            }

            text = default;
            return false;
        }

        private static void IndexName(Dictionary<string, string> map, string? name, string english)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(english))
            {
                return;
            }

            map[name.Trim()] = english;
        }

        private sealed class Snapshot
        {
            public DateTime ExtractedUtc { get; set; }
            public DateTime NamesUtc { get; set; }
            public DateTime GgpkWriteUtc { get; set; }
            public string GgpkPath { get; set; } = string.Empty;
            public List<CatalogItem> Items { get; set; } = new();
            public List<CatalogMod> Mods { get; set; } = new();
            public List<CatalogArea> Areas { get; set; } = new();
        }
    }
}
