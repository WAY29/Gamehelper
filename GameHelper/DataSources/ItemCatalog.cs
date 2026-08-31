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

    public sealed class CatalogText
    {
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
        private static Dictionary<string, CatalogText> itemI18n = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, CatalogText> modI18n = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, CatalogText> areaI18n = new(StringComparer.OrdinalIgnoreCase);
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

                SetProgress(0, 8, "open");
                byte[] itemsDat;
                byte[] modsDat;
                byte[] areasDat;
                byte[] artDat;
                byte[] statsDat;
                byte[] csdMain;
                byte[] csdMap;
                byte[] csdAtlas;
                byte[] csdTablet;
                byte[] itemsTwDat;
                byte[] areasTwDat;
                using (OpenIndex(path, out var index))
                {
                    SetProgress(1, 8, "read");
                    itemsDat = ReadDat(index, "Data/Balance/BaseItemTypes.datc64");
                    itemsTwDat = TryReadDat(index, "Data/Balance/Traditional Chinese/BaseItemTypes.datc64");
                    SetProgress(2, 8, "read");
                    modsDat = ReadDat(index, "Data/Balance/Mods.datc64");
                    statsDat = TryReadDat(index, "Data/Balance/Stats.datc64");
                    SetProgress(3, 8, "read");
                    areasDat = ReadDat(index, "Data/Balance/WorldAreas.datc64");
                    areasTwDat = TryReadDat(index, "Data/Balance/Traditional Chinese/WorldAreas.datc64");
                    SetProgress(4, 8, "read");
                    artDat = ReadDat(index, "Data/Balance/ItemVisualIdentity.datc64");
                    SetProgress(5, 8, "read");
                    csdMain = TryReadDat(index, "Data/StatDescriptions/stat_descriptions.csd");
                    csdMap = TryReadDat(index, "Data/StatDescriptions/map_stat_descriptions.csd");
                    csdAtlas = TryReadDat(index, "Data/StatDescriptions/atlas_stat_descriptions.csd");
                    csdTablet = TryReadDat(index, "Data/StatDescriptions/tablet_stat_descriptions.csd");
                }

                SetProgress(6, 8, "parse");
                var items = Datc64Strings.ParseBaseItems(itemsDat);
                if (items.Count == 0)
                {
                    throw new InvalidOperationException("BaseItemTypes had no Metadata/Items paths");
                }

                Datc64Strings.ApplyArt(items, itemsDat, artDat);

                Dictionary<string, CatalogItem> previousItems;
                Dictionary<string, CatalogMod> previousMods;
                Dictionary<string, CatalogArea> previousAreas;
                Dictionary<string, CatalogText> itemLoc;
                Dictionary<string, CatalogText> modLoc;
                Dictionary<string, CatalogText> areaLoc;
                DateTime previousNamesUtc;
                lock (Gate)
                {
                    previousItems = byInternal;
                    previousMods = byMod;
                    previousAreas = byArea;
                    itemLoc = itemI18n;
                    modLoc = modI18n;
                    areaLoc = areaI18n;
                    previousNamesUtc = namesUtc;
                }

                foreach (var item in items)
                {
                    ApplyItemLoc(item, previousItems, itemLoc);
                }

                Datc64Strings.OverlayItemZhTw(items, itemsTwDat);

                var oldNames = NamesFromMods(previousMods.Values);
                foreach (var (id, text) in modLoc)
                {
                    oldNames[id] = (text.English, text.ZhCn, text.ZhTw);
                }

                var mods = new List<CatalogMod>();
                foreach (var family in Datc64Strings.ParseModFamilies(modsDat))
                {
                    mods.Add(new CatalogMod { Id = family });
                }

                StatDescriptions.Apply(mods, modsDat, statsDat, csdMain, csdMap, csdAtlas, csdTablet);
                foreach (var mod in mods)
                {
                    if (!string.IsNullOrEmpty(mod.English) || !TryModText(oldNames, mod.Id, out var text))
                    {
                        continue;
                    }

                    mod.English = text.En;
                    mod.ZhCn = text.ZhCn;
                    mod.ZhTw = text.ZhTw;
                }

                var areas = Datc64Strings.ParseWorldAreas(areasDat);
                foreach (var area in areas)
                {
                    if (previousAreas.TryGetValue(area.Id, out var oldArea) &&
                        string.Equals(oldArea.English, area.English, StringComparison.Ordinal))
                    {
                        area.ZhCn = oldArea.ZhCn;
                        area.ZhTw = oldArea.ZhTw;
                    }

                    if (areaLoc.TryGetValue(area.Id, out var loc))
                    {
                        area.ZhCn = loc.ZhCn;
                    }
                }

                Datc64Strings.OverlayAreaZhTw(areas, areasTwDat);

                SetProgress(7, 8, "parse");
                SetProgress(8, 8, "save");
                SaveAndApply(new Snapshot
                {
                    ExtractedUtc = DateTime.UtcNow,
                    NamesUtc = previousNamesUtc,
                    GgpkWriteUtc = File.GetLastWriteTimeUtc(path),
                    GgpkPath = path,
                    Items = items,
                    Mods = mods,
                    Areas = areas,
                    ItemI18n = itemLoc,
                    ModI18n = modLoc,
                    AreaI18n = areaLoc,
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
                Dictionary<string, CatalogText> itemLoc;
                Dictionary<string, CatalogText> modLoc;
                Dictionary<string, CatalogText> areaLoc;
                DateTime extracted;
                DateTime ggpkWrite;
                string path;
                lock (Gate)
                {
                    items = new List<CatalogItem>(byInternal.Values);
                    mods = new List<CatalogMod>(byMod.Values);
                    areas = new List<CatalogArea>(byArea.Values);
                    itemLoc = new Dictionary<string, CatalogText>(itemI18n, StringComparer.OrdinalIgnoreCase);
                    modLoc = new Dictionary<string, CatalogText>(modI18n, StringComparer.OrdinalIgnoreCase);
                    areaLoc = new Dictionary<string, CatalogText>(areaI18n, StringComparer.OrdinalIgnoreCase);
                    extracted = extractedUtc;
                    ggpkWrite = ggpkWriteUtc;
                    path = ggpkPath;
                }

                SetProgress(0, Poe2dbNames.PageCount + Poe2dbMaps.PageCount, "names");
                var names = Poe2dbNames.FetchEnglishToLocalAsync(TickProgress).GetAwaiter().GetResult();
                foreach (var (en, loc) in names)
                {
                    itemLoc[en] = new CatalogText { English = en, ZhCn = loc.ZhCn, ZhTw = loc.ZhTw };
                }

                foreach (var item in items)
                {
                    if (item.English.Length > 0 && itemLoc.TryGetValue(item.English, out var loc))
                    {
                        ApplyFetchedItemName(item, loc);
                    }
                }

                var mapNames = Poe2dbMaps.FetchAsync(areas, TickProgress).GetAwaiter().GetResult();
                foreach (var (id, loc) in mapNames)
                {
                    areaLoc[id] = new CatalogText { ZhCn = loc.ZhCn, ZhTw = loc.ZhTw };
                }

                foreach (var area in areas)
                {
                    if (!areaLoc.TryGetValue(area.Id, out var loc))
                    {
                        continue;
                    }

                    area.ZhCn = loc.ZhCn;
                    if (area.ZhTw.Length == 0)
                    {
                        area.ZhTw = loc.ZhTw;
                    }
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
                    ItemI18n = itemLoc,
                    ModI18n = modLoc,
                    AreaI18n = areaLoc,
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
                lastError = MissingOo2Core(ex) ? "oo2core" : Innermost(ex);
            }

            Console.WriteLine($"[ItemCatalog] {ex}");
        }

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            return ex.Message;
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

        private static byte[] TryReadDat(LibBundle3.Index index, string datPath)
        {
            if (!index.TryGetFile(datPath, out var file) || file is null)
            {
                return [];
            }

            return file.Read().ToArray();
        }

        private static byte[] ReadDat(LibBundle3.Index index, string datPath)
        {
            var bytes = TryReadDat(index, datPath);
            if (bytes.Length == 0)
            {
                throw new FileNotFoundException(datPath);
            }

            return bytes;
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
                itemI18n = snapshot.ItemI18n ?? itemI18n;
                modI18n = snapshot.ModI18n ?? modI18n;
                areaI18n = snapshot.AreaI18n ?? areaI18n;
                lastError = string.Empty;
                extractedUtc = snapshot.ExtractedUtc;
                namesUtc = snapshot.NamesUtc;
                ggpkWriteUtc = snapshot.GgpkWriteUtc;
                ggpkPath = snapshot.GgpkPath ?? string.Empty;
                loaded = true;
            }
        }

        private static Dictionary<string, (string En, string ZhCn, string ZhTw)> NamesFromMods(
            IEnumerable<CatalogMod> rows)
        {
            var names = new Dictionary<string, (string En, string ZhCn, string ZhTw)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.English))
                {
                    continue;
                }

                names[row.Id] = (row.English, row.ZhCn, row.ZhTw);
            }

            return names;
        }

        private static void ApplyModNames(
            List<CatalogMod> mods,
            Dictionary<string, (string En, string ZhCn, string ZhTw)> names)
        {
            var byId = new Dictionary<string, CatalogMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods)
            {
                if (!string.IsNullOrEmpty(mod.Id))
                {
                    byId[mod.Id] = mod;
                }
            }

            foreach (var (id, text) in names)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (!byId.TryGetValue(id, out var mod))
                {
                    mod = new CatalogMod { Id = id };
                    mods.Add(mod);
                    byId[id] = mod;
                }

                mod.English = text.En;
                mod.ZhCn = text.ZhCn;
                mod.ZhTw = text.ZhTw;
            }

            foreach (var mod in mods)
            {
                if (TryModText(names, mod.Id, out var text))
                {
                    mod.English = text.En;
                    mod.ZhCn = text.ZhCn;
                    mod.ZhTw = text.ZhTw;
                }
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
            public Dictionary<string, CatalogText> ItemI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CatalogText> ModI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CatalogText> AreaI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static void ApplyItemLoc(
            CatalogItem item,
            Dictionary<string, CatalogItem> previousItems,
            Dictionary<string, CatalogText> itemLoc)
        {
            if (previousItems.TryGetValue(item.InternalName, out var old) &&
                string.Equals(old.English, item.English, StringComparison.Ordinal))
            {
                item.ZhCn = old.ZhCn;
                item.ZhTw = old.ZhTw;
            }

            if (item.English.Length > 0 && itemLoc.TryGetValue(item.English, out var loc))
            {
                ApplyFetchedItemName(item, loc);
            }
        }

        private static void ApplyFetchedItemName(CatalogItem item, CatalogText loc)
        {
            if (!string.IsNullOrEmpty(loc.ZhCn))
            {
                item.ZhCn = loc.ZhCn;
            }

            if (item.ZhTw.Length == 0 && !string.IsNullOrEmpty(loc.ZhTw))
            {
                item.ZhTw = loc.ZhTw;
            }
        }
    }
}
