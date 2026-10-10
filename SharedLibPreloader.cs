using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RIR_PluginManager
{
    /// Предзагрузка общих библиотек плагинов.
    ///
    /// В одном процессе .NET может быть только одна версия сборки с данным именем, и плагину
    /// подходит уже загруженная версия, только если она не старше нужной. Если несколько плагинов
    /// привозят одну библиотеку в разных версиях, то без вмешательства побеждает тот, кто загрузился
    /// первым, и плагины, которым нужна более новая версия, не загружаются.
    /// Здесь до загрузки плагинов Grasshopper заранее загружается самая новая из найденных копий.
    public static class SharedLibPreloader
    {
        public sealed class Item
        {
            public string Name;
            public Version Version;
            public string Path;
            public string OwnerName;                                   // плагин, из которого берётся копия
            public List<(string owner, Version version)> Older = new List<(string, Version)>();
            public bool MajorDiffers => Older.Any(o => o.version.Major != Version.Major);
        }

        static bool _done;
        static readonly object _lock = new object();

        /// План: какие библиотеки и из каких файлов будут загружены заранее.
        /// disabledKeys — ключи отключённых плагинов (их файлы не учитываются).
        public static List<Item> Plan(IList<PluginGroup> groups, ISet<string> disabledKeys,
                                      ISet<string> exclude, Dictionary<string, Version> loaded)
        {
            // имя сборки -> копии (версия, путь, плагин)
            var copies = new Dictionary<string, List<(Version ver, string path, string ownerKey, string ownerName)>>(StringComparer.OrdinalIgnoreCase);

            void Add(string dll, string ownerKey, string ownerName)
            {
                var an = ReadAssemblyName(dll);                          // только метаданные, код не выполняется
                if (an.name == null || an.version == null) return;       // нативная DLL или повреждённый файл
                if (!copies.TryGetValue(an.name, out var list))
                    copies[an.name] = list = new List<(Version, string, string, string)>();
                list.Add((an.version, dll, ownerKey, ownerName));
            }

            foreach (var g in groups)
            {
                if (disabledKeys != null && disabledKeys.Contains(g.Key)) continue;
                // Плагин, который не загрузится в этом сеансе (рантайм или версия Rhino), не должен
                // навязывать свои версии библиотек остальным
                if (PluginChecker.IsIncompatibleGroup(g)) continue;
                foreach (var dll in PluginChecker.GroupDlls(g))
                    if (!PluginChecker.IsIncompatibleAssembly(dll)) Add(dll, g.Key, g.Name);   // без папок другого рантайма
            }
            foreach (var dll in PluginStore.RootLibraryDlls()) Add(dll, "", L.T("Libraries (корень)", "Libraries (root)"));

            var plan = new List<Item>();
            foreach (var kv in copies)
            {
                var name = kv.Key;
                var list = kv.Value;
                if (exclude != null && exclude.Contains(name)) continue;
                if (PluginChecker.IsFrameworkAssembly(name)) continue;      // системные сборки даёт сам рантайм
                // .NET Framework: версии системных библиотек и библиотек Microsoft задаёт Revit.exe.config
                // (binding redirects), загружать их копии заранее нельзя
                if (Session.IsNetFramework &&
                    (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("netstandard", StringComparison.OrdinalIgnoreCase))) continue;
                if (loaded != null && loaded.ContainsKey(name)) continue;   // уже загружена (Revit, Rhino, надстройки)

                var owners = list.Select(c => c.ownerKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var versions = list.Select(c => c.ver).Distinct().Count();
                if (owners < 2 || versions < 2) continue;                  // конфликта нет

                var best = list.OrderByDescending(c => c.ver)
                               .ThenByDescending(c => SafeWriteTime(c.path))
                               .First();
                var item = new Item { Name = name, Version = best.ver, Path = best.path, OwnerName = best.ownerName };
                foreach (var c in list.Where(c => c.ver < best.ver)
                                      .GroupBy(c => c.ownerName)
                                      .Select(gr => gr.OrderByDescending(x => x.ver).First()))
                    item.Older.Add((c.ownerName, c.ver));
                if (item.Older.Count > 0) plan.Add(item);
            }
            return plan.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// Имя и версия сборки без исключений для нативных DLL
        /// (AssemblyName.GetAssemblyName на них бросает BadImageFormatException и засоряет лог).
        static (string name, Version version) ReadAssemblyName(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var pe = new System.Reflection.PortableExecutable.PEReader(fs))
                {
                    if (!pe.HasMetadata) return (null, null);
                    var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                    if (!md.IsAssembly) return (null, null);
                    var def = md.GetAssemblyDefinition();
                    return (md.GetString(def.Name), def.Version);
                }
            }
            catch
            {
                return (null, null);
            }
        }

        static DateTime SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
        }

        /// Выполнить предзагрузку (один раз за сессию). Безопасно вызывать повторно.
        public static void Run(string reason)
        {
            lock (_lock)
            {
                if (_done) return;
                _done = true;
            }

            try
            {
                if (PluginStore.GrasshopperPluginsLoaded())
                {
                    PluginStore.Log(L.T("Предзагрузка: пропущена, Grasshopper уже загрузил плагины", "Preload: skipped, Grasshopper has already loaded plugins"));
                    return;
                }

                var profile = PluginStore.LoadProfile();
                if (!profile.PreloadShared)
                {
                    PluginStore.Log(L.T("Предзагрузка общих библиотек выключена в настройках", "Preloading of shared libraries is turned off in settings"));
                    return;
                }

                var plan = Plan(PluginStore.Scan(), profile.Disabled, profile.PreloadExclude, PluginChecker.SnapshotLoaded());
                PluginStore.Log(L.T($"Предзагрузка общих библиотек ({reason}): найдено {plan.Count}", $"Preloading shared libraries ({reason}): found {plan.Count}"));

                foreach (var item in plan)
                {
                    var older = string.Join(", ", item.Older.Select(o => $"{o.owner} {o.version}"));
                    try
                    {
                        var asm = Assembly.LoadFrom(item.Path);
#if NETFRAMEWORK
                        var ctx = "LoadFrom";
#else
                        var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm)?.Name ?? "?";
#endif
                        PluginStore.Log(L.T($"  {item.Name} {item.Version} из {item.OwnerName} загружена заранее (контекст {ctx}); более старые копии: {older}",
                                            $"  {item.Name} {item.Version} from {item.OwnerName} preloaded (context {ctx}); older copies: {older}") +
                                        (item.MajorDiffers ? L.T("; основной номер версии отличается — возможна несовместимость",
                                                                 "; the major version differs, incompatibility is possible") : ""));
                    }
                    catch (Exception ex)
                    {
                        PluginStore.Log(L.T($"  {item.Name} {item.Version} из {item.OwnerName}: не удалось загрузить заранее: {ex.Message}",
                                            $"  {item.Name} {item.Version} from {item.OwnerName}: could not preload: {ex.Message}"));
                    }
                }
            }
            catch (Exception ex)
            {
                PluginStore.Log(L.T("Предзагрузка: ошибка ", "Preload: error ") + ex);
            }
        }
    }
}
