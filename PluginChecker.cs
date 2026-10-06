using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;

namespace RIR_PluginManager
{
    public enum IssueLevel { Info = 0, Warning = 1, Error = 2 }

    public sealed class Issue
    {
        public IssueLevel Level { get; init; }
        public string Text { get; init; }
    }

    /// Проверка плагинов БЕЗ их загрузки: файлы читаются как данные (метаданные PE/.NET),
    /// код плагинов не выполняется, поэтому проверка не может уронить Revit.
    public static class PluginChecker
    {
        sealed class FileFacts
        {
            public bool IsManaged;
            public string AsmName;
            public Version AsmVersion;
            public string TargetFramework;
            public bool UsesBinaryFormatter;
            public bool UsesIronPython;
            public int SerializedResources;                 // всего ресурсов в формате BinaryFormatter
            public int UnsupportedResources;                // из них не восстанавливаемых IconFix
            public List<string> UnsupportedTypes = new List<string>();
            public string Error;
        }

        // Кэш на время сессии Revit: файл перепроверяется, только если изменился
        static readonly Dictionary<string, (DateTime time, long size, FileFacts facts)> _cache =
            new Dictionary<string, (DateTime, long, FileFacts)>(StringComparer.OrdinalIgnoreCase);

        /// Версии сборок, уже загруженных в процесс Revit (снимок делается в UI-потоке).
        public static Dictionary<string, Version> SnapshotLoaded()
        {
            var d = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var n = a.GetName();
                    if (n.Name == null || n.Version == null) continue;
                    if (!d.TryGetValue(n.Name, out var v) || n.Version > v) d[n.Name] = n.Version;
                }
                catch { }
            }
            return d;
        }

        public static Dictionary<string, List<Issue>> Check(IList<PluginGroup> groups, Dictionary<string, Version> loaded, bool iconFixActive,
                                                             Dictionary<string, SharedLibPreloader.Item> preload = null)
        {
            var result = groups.ToDictionary(g => g.Key, g => new List<Issue>(), StringComparer.OrdinalIgnoreCase);

            // имя сборки -> кто её содержит
            var owners = new Dictionary<string, List<(Version ver, string ownerKey, string ownerName, bool isGha)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var issues = result[g.Key];

                foreach (var f in g.Files)
                {
                    var actual = ActualPath(f);
                    if (actual == null) continue;

                    if (f.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new Issue
                        {
                            Level = IssueLevel.Error,
                            Text = $"{Path.GetFileName(f)}: Python-компоненты (.ghpy, IronPython 2.7) на .NET 10 не загружаются, Grasshopper покажет окно ошибки"
                        });
                        continue;
                    }

                    var facts = Analyze(actual);
                    AddIssues(facts, f, issues, dependency: false, iconFixActive);
                    if (facts.IsManaged && facts.AsmName != null)
                        Register(owners, facts, g, isGha: true);
                }

                foreach (var dll in GroupDlls(g))
                {
                    var facts = Analyze(dll);
                    if (!facts.IsManaged) continue;
                    AddIssues(facts, dll, issues, dependency: true, iconFixActive);
                    if (facts.AsmName == null) continue;
                    Register(owners, facts, g, isGha: false);

                    if (!IsFrameworkAssembly(facts.AsmName) &&
                        loaded.TryGetValue(facts.AsmName, out var lv) && facts.AsmVersion > lv)
                    {
                        issues.Add(new Issue
                        {
                            Level = IssueLevel.Warning,
                            Text = $"{facts.AsmName} {facts.AsmVersion}: в процессе Revit уже загружена версия {lv}, " +
                                   "более новая копия не загрузится, плагин может не работать"
                        });
                    }
                }
            }

            // Общие DLL в корне Libraries
            foreach (var dll in PluginStore.RootLibraryDlls())
            {
                var facts = Analyze(dll);
                if (!facts.IsManaged || facts.AsmName == null) continue;
                if (!owners.TryGetValue(facts.AsmName, out var list))
                    owners[facts.AsmName] = list = new List<(Version, string, string, bool)>();
                list.Add((facts.AsmVersion, "", "Libraries (корень)", false));
            }

            // Конфликты версий и дубликаты.
            // В процессе может быть только одна версия сборки с данным именем, и .NET подставляет
            // уже загруженную, если она не старше запрошенной. Поэтому риск есть только у плагина,
            // которому нужна БОЛЕЕ НОВАЯ версия, чем у кого-то ещё (или чем уже загружена в Revit).
            // Замечание пишется только этому плагину и только про его собственную сборку.
            foreach (var kv in owners)
            {
                if (IsFrameworkAssembly(kv.Key)) continue;          // системные сборки .NET берутся из самого рантайма
                var entries = kv.Value;
                loaded.TryGetValue(kv.Key, out var loadedVer);

                foreach (var g in groups)
                {
                    var mine = entries.Where(e => string.Equals(e.ownerKey, g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (mine.Count == 0) continue;
                    var others = entries.Where(e => !string.Equals(e.ownerKey, g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (others.Count == 0) continue;
                    var myVer = mine.Max(e => e.ver);

                    // Этот же плагин установлен второй раз (одна и та же .gha в двух местах)
                    if (mine.Any(e => e.isGha) && others.Any(e => e.isGha))
                    {
                        result[g.Key].Add(new Issue
                        {
                            Level = IssueLevel.Warning,
                            Text = $"{kv.Key}: этот плагин установлен ещё раз ({string.Join(", ", others.Where(e => e.isGha).Select(e => e.ownerName).Distinct())})"
                        });
                        continue;
                    }

                    if (loadedVer != null && loadedVer >= myVer) continue;   // в Revit уже есть подходящая версия

                    // Конфликт решается предзагрузкой новейшей версии
                    if (preload != null && preload.TryGetValue(kv.Key, out var pre))
                    {
                        if (myVer >= pre.Version) continue;                // этот плагин сам даёт новейшую копию
                        bool major = myVer.Major != pre.Version.Major;
                        result[g.Key].Add(new Issue
                        {
                            Level = major ? IssueLevel.Warning : IssueLevel.Info,
                            Text = $"{kv.Key} {myVer}: будет использоваться версия {pre.Version} из {pre.OwnerName} (предзагрузка общих библиотек)" +
                                   (major ? ". Основной номер версии отличается — возможна несовместимость; " +
                                            $"при проблемах добавьте в профиль preload_exclude={kv.Key}" : "")
                        });
                        continue;
                    }
                    var older = others.Where(e => e.ver < myVer)
                                      .OrderBy(e => e.ver)
                                      .Select(e => $"{e.ownerName} {e.ver}")
                                      .Distinct()
                                      .ToList();
                    if (older.Count == 0) continue;

                    result[g.Key].Add(new Issue
                    {
                        Level = IssueLevel.Warning,
                        Text = $"{kv.Key} {myVer}: плагину нужна эта версия, а у {string.Join(", ", older)} она старше. " +
                               "Если старая загрузится раньше, этот плагин может не загрузиться"
                    });
                }
            }

            return result;
        }

        static HashSet<string> _frameworkNames;

        /// Сборка входит в рантайм .NET или Windows Desktop: её всегда даёт сам рантайм, копия плагина не используется.
        internal static bool IsFrameworkAssembly(string name)
        {
            if (_frameworkNames == null)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in new[]
                {
                    System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location)
                })
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            foreach (var f in Directory.EnumerateFiles(dir, "*.dll"))
                                set.Add(Path.GetFileNameWithoutExtension(f));
                    }
                    catch { }
                }
                _frameworkNames = set;
            }
            return _frameworkNames.Contains(name);
        }

        static void Register(Dictionary<string, List<(Version, string, string, bool)>> owners, FileFacts facts, PluginGroup g, bool isGha)
        {
            if (!owners.TryGetValue(facts.AsmName, out var list))
                owners[facts.AsmName] = list = new List<(Version, string, string, bool)>();
            list.Add((facts.AsmVersion, g.Key, g.Name, isGha));
        }

        static void AddIssues(FileFacts facts, string file, List<Issue> issues, bool dependency, bool iconFixActive)
        {
            var name = Path.GetFileName(file);
            if (facts.Error != null)
            {
                issues.Add(new Issue { Level = IssueLevel.Info, Text = $"{name}: не удалось проверить ({facts.Error})" });
                return;
            }
            if (!facts.IsManaged) return;

            if (dependency)
            {
                // Для DLL-зависимостей ссылки на BinaryFormatter и старые ресурсы форм — шум:
                // они срабатывают, только если плагин откроет соответствующий код. Оставляем лишь IronPython.
                if (facts.UsesIronPython)
                    issues.Add(new Issue
                    {
                        Level = IssueLevel.Warning,
                        Text = $"{name}: зависит от IronPython, на .NET 10 Python-часть, скорее всего, не работает"
                    });
                return;
            }

            if (facts.UsesBinaryFormatter)
                issues.Add(new Issue
                {
                    Level = IssueLevel.Warning,
                    Text = $"{name}: код плагина использует BinaryFormatter (удалён в .NET 9+): " +
                           "функции, которые к нему обращаются, работать не будут"
                });

            if (facts.UsesIronPython)
                issues.Add(new Issue
                {
                    Level = IssueLevel.Warning,
                    Text = $"{name}: зависит от IronPython, на .NET 10 Python-часть, скорее всего, не работает"
                });

            if (!iconFixActive)
            {
                if (facts.SerializedResources > 0)
                    issues.Add(new Issue
                    {
                        Level = IssueLevel.Warning,
                        Text = $"{name}: ресурсов в формате BinaryFormatter: {facts.SerializedResources} (обычно иконки). " +
                               "Не загрузятся; при запуске GH штатной кнопкой возможен краш Revit. " +
                               "Включите восстановление иконок в окне надстройки"
                    });
            }
            else if (facts.UnsupportedResources > 0)
            {
                // Картинки и иконки восстанавливает IconFix, в критерии остаются только прочие типы
                issues.Add(new Issue
                {
                    Level = IssueLevel.Warning,
                    Text = $"{name}: ресурсов в формате BinaryFormatter, которые не восстанавливаются: {facts.UnsupportedResources} " +
                           $"({string.Join(", ", facts.UnsupportedTypes)})"
                });
            }

            if (!dependency)
            {
                if (facts.TargetFramework == null)
                    issues.Add(new Issue { Level = IssueLevel.Info, Text = $"{name}: целевой рантайм не указан (очень старая сборка)" });
                else if (facts.TargetFramework.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new Issue { Level = IssueLevel.Info, Text = $"{name}: собран под {facts.TargetFramework}, обычно работает, но не гарантированно" });
            }
        }

        static string ActualPath(string f)
        {
            if (File.Exists(f)) return f;
            if (File.Exists(f + PluginStore.OffSuffix)) return f + PluginStore.OffSuffix;
            return null;
        }

        internal static IEnumerable<string> GroupDlls(PluginGroup g)
        {
            if (g.Folder == null || !Directory.Exists(g.Folder)) return Array.Empty<string>();
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                return Directory.EnumerateFiles(g.Folder, "*.dll", options).ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        static FileFacts Analyze(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                lock (_cache)
                {
                    if (_cache.TryGetValue(path, out var c) && c.time == fi.LastWriteTimeUtc && c.size == fi.Length)
                        return c.facts;
                }
                FileFacts facts;
                try { facts = AnalyzeCore(path); }
                catch (Exception ex) { facts = new FileFacts { Error = ex.Message }; }
                lock (_cache) _cache[path] = (fi.LastWriteTimeUtc, fi.Length, facts);
                return facts;
            }
            catch (Exception ex)
            {
                return new FileFacts { Error = ex.Message };
            }
        }

        static FileFacts AnalyzeCore(string path)
        {
            var facts = new FileFacts();
            var bytes = File.ReadAllBytes(path);
            using (var pe = new PEReader(ImmutableArray.Create(bytes)))
            {
                if (!pe.HasMetadata) return facts;               // нативная DLL
                var md = pe.GetMetadataReader();
                facts.IsManaged = true;

                if (md.IsAssembly)
                {
                    var ad = md.GetAssemblyDefinition();
                    facts.AsmName = md.GetString(ad.Name);
                    facts.AsmVersion = ad.Version;

                    foreach (var h in ad.GetCustomAttributes())
                    {
                        var ca = md.GetCustomAttribute(h);
                        if (AttributeTypeName(md, ca) != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
                        try
                        {
                            var br = md.GetBlobReader(ca.Value);
                            if (br.ReadUInt16() == 1) facts.TargetFramework = br.ReadSerializedString();
                        }
                        catch { }
                    }
                }

                foreach (var h in md.TypeReferences)
                {
                    var tr = md.GetTypeReference(h);
                    if (md.StringComparer.Equals(tr.Name, "BinaryFormatter") &&
                        md.StringComparer.Equals(tr.Namespace, "System.Runtime.Serialization.Formatters.Binary"))
                    {
                        facts.UsesBinaryFormatter = true;
                        break;
                    }
                }

                foreach (var h in md.AssemblyReferences)
                {
                    var ar = md.GetAssemblyReference(h);
                    if (md.GetString(ar.Name).StartsWith("IronPython", StringComparison.OrdinalIgnoreCase))
                    {
                        facts.UsesIronPython = true;
                        break;
                    }
                }

                CountSerializedResources(pe, md, facts);
            }
            return facts;
        }

        static string AttributeTypeName(MetadataReader md, CustomAttribute ca)
        {
            EntityHandle type;
            if (ca.Constructor.Kind == HandleKind.MemberReference)
                type = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
            else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
                type = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();
            else
                return null;

            if (type.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)type);
                return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            }
            if (type.Kind == HandleKind.TypeDefinition)
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)type);
                return md.GetString(td.Namespace) + "." + md.GetString(td.Name);
            }
            return null;
        }

        /// Считает ресурсы, которые .NET может прочитать только через BinaryFormatter
        /// (картинки, иконки и другие объекты в старом формате .resources).
        /// Значения не десериализуются: читаются только имена типов.
        static void CountSerializedResources(PEReader pe, MetadataReader md, FileFacts facts)
        {
            var cor = pe.PEHeaders.CorHeader;
            if (cor == null || cor.ResourcesDirectory.Size == 0) return;
            int baseRva = cor.ResourcesDirectory.RelativeVirtualAddress;

            foreach (var h in md.ManifestResources)
            {
                var res = md.GetManifestResource(h);
                if (!res.Implementation.IsNil) continue;               // ресурс во внешнем файле
                var name = md.GetString(res.Name);
                if (!name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var block = pe.GetSectionData(baseRva + (int)res.Offset);
                    var r = block.GetReader();
                    int length = r.ReadInt32();
                    var data = r.ReadBytes(length);

                    using (var reader = new ResourceReader(new MemoryStream(data)))
                    {
                        var en = reader.GetEnumerator();
                        while (en.MoveNext())
                        {
                            if (!(en.Key is string key)) continue;
                            reader.GetResourceData(key, out string type, out _);
                            if (type == null || type.StartsWith("ResourceTypeCode.", StringComparison.Ordinal)) continue;

                            facts.SerializedResources++;
                            if (!IconFix.IsSupportedType(type))
                            {
                                facts.UnsupportedResources++;
                                var shortType = type.Split(',')[0].Trim();
                                if (!facts.UnsupportedTypes.Contains(shortType)) facts.UnsupportedTypes.Add(shortType);
                            }
                        }
                    }
                }
                catch
                {
                    // Другой формат (например, System.Resources.Extensions): BinaryFormatter не нужен
                }
            }
        }
    }
}
