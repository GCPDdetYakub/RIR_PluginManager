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

    /// Условия текущего сеанса, от которых зависят критерии проверки.
    public sealed class CheckContext
    {
        public bool NetFramework = Session.IsNetFramework;   // Revit 2021–2024: Rhino работает на .NET Framework 4.8
        public int RuntimeMajor = Session.RuntimeMajor;       // 4 для .NET Framework, 10 для Revit 2025.5+
        public int RhinoMajor = Session.RhinoMajor;           // 0 — не определена
        public bool IconFixActive = IconFix.Active;
        public Dictionary<string, SharedLibPreloader.Item> Preload;
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
            public int CoreRuntimeMajor;                    // >0: собран под .NET Core / .NET 5+ (основной номер)
            public int RhinoSdkMajor;                       // основная версия RhinoCommon/Grasshopper, на которую ссылается сборка
            public bool StrongNamed;                        // подписанная сборка (есть открытый ключ)
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

        public static Dictionary<string, List<Issue>> Check(IList<PluginGroup> groups, Dictionary<string, Version> loaded, CheckContext ctx)
        {
            var preload = ctx.Preload;
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
                    if (IsInactiveTargetFolder(f)) continue;      // сборка для другого рантайма в пакете с несколькими

                    if (f.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ctx.RuntimeMajor < 9) continue;           // на .NET Framework и .NET 8 IronPython работает
                        issues.Add(new Issue
                        {
                            Level = IssueLevel.Error,
                            Text = $"{Path.GetFileName(f)}: Python-компоненты (.ghpy, IronPython 2.7) на .NET {ctx.RuntimeMajor} не загружаются, Grasshopper покажет окно ошибки"
                        });
                        continue;
                    }

                    var facts = Analyze(actual);
                    AddIssues(facts, f, issues, dependency: false, ctx);
                    if (facts.IsManaged && facts.AsmName != null)
                        Register(owners, facts, g, isGha: true);
                }

                bool groupIncompatible = IsIncompatibleGroup(g);
                foreach (var dll in GroupDlls(g))
                {
                    var facts = Analyze(dll);
                    if (!facts.IsManaged) continue;
                    // У несовместимого плагина замечания по его библиотекам — лишний шум: он не загрузится целиком
                    if (!groupIncompatible) AddIssues(facts, dll, issues, dependency: true, ctx);
                    if (groupIncompatible) continue;
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
                                            $"при проблемах добавьте в settings-revit{PluginStore.RevitVersion}.txt строку preload_exclude={kv.Key}" : "")
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

        static void AddIssues(FileFacts facts, string file, List<Issue> issues, bool dependency, CheckContext ctx)
        {
            var name = Path.GetFileName(file);
            if (facts.Error != null)
            {
                issues.Add(new Issue { Level = IssueLevel.Info, Text = $"{name}: не удалось проверить ({facts.Error})" });
                return;
            }
            if (!facts.IsManaged) return;

            // Сборка под более новый рантайм, чем у процесса Revit (например, .NET 7/8 в Revit 2021–2024)
            if (facts.CoreRuntimeMajor > 0 && (ctx.NetFramework || facts.CoreRuntimeMajor > ctx.RuntimeMajor))
            {
                var host = ctx.NetFramework ? ".NET Framework 4.8" : $".NET {ctx.RuntimeMajor}";
                issues.Add(new Issue
                {
                    Level = dependency ? IssueLevel.Warning : IssueLevel.Error,
                    Text = dependency
                        ? $"{name}: библиотека собрана под .NET {facts.CoreRuntimeMajor}, а Rhino в этой версии Revit работает на {host}: " +
                          "функции плагина, которые её используют, работать не будут"
                        : $"{name}: собран под .NET {facts.CoreRuntimeMajor}, а Rhino в этой версии Revit работает на {host}: " +
                          "плагин не загрузится или не будет работать. Если он выполняет код при открытии Grasshopper, может уронить Revit"
                });
            }

            // Сборка для более новой версии Rhino, чем та, с которой работает Rhino.Inside
            if (!dependency && ctx.RhinoMajor > 0 && facts.RhinoSdkMajor > ctx.RhinoMajor)
                issues.Add(new Issue
                {
                    Level = IssueLevel.Error,
                    Text = $"{name}: собран для Rhino {facts.RhinoSdkMajor} (RhinoCommon/Grasshopper {facts.RhinoSdkMajor}), " +
                           $"а Rhino.Inside работает с Rhino {ctx.RhinoMajor}: плагин не загрузится"
                });

            // Остальные критерии касаются только .NET 8/10 (Revit 2025+)
            if (ctx.NetFramework) return;

            if (dependency)
            {
                // Для DLL-зависимостей ссылки на BinaryFormatter и старые ресурсы форм — шум:
                // они срабатывают, только если плагин откроет соответствующий код. Оставляем лишь IronPython.
                if (facts.UsesIronPython && ctx.RuntimeMajor >= 9)
                    issues.Add(new Issue
                    {
                        Level = IssueLevel.Warning,
                        Text = $"{name}: зависит от IronPython, на .NET {ctx.RuntimeMajor} Python-часть, скорее всего, не работает"
                    });
                return;
            }

            if (facts.UsesBinaryFormatter && ctx.RuntimeMajor >= 9)
                issues.Add(new Issue
                {
                    Level = IssueLevel.Warning,
                    Text = $"{name}: код плагина использует BinaryFormatter (удалён в .NET 9+): " +
                           "функции, которые к нему обращаются, работать не будут"
                });

            if (facts.UsesIronPython && ctx.RuntimeMajor >= 9)
                issues.Add(new Issue
                {
                    Level = IssueLevel.Warning,
                    Text = $"{name}: зависит от IronPython, на .NET {ctx.RuntimeMajor} Python-часть, скорее всего, не работает"
                });

            if (ctx.RuntimeMajor < 9)
            {
                // BinaryFormatter есть в рантайме — ресурсы читаются как обычно
            }
            else if (!ctx.IconFixActive)
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

        /// Сборка под рантайм новее, чем у процесса Revit, или для более новой версии Rhino — она не загрузится.
        internal static bool IsIncompatibleAssembly(string path)
        {
            var f = Analyze(path);
            if (!f.IsManaged) return false;
            if (f.CoreRuntimeMajor > 0 && (Session.IsNetFramework || f.CoreRuntimeMajor > Session.RuntimeMajor)) return true;
            int rhino = Session.RhinoMajor;
            return rhino > 0 && f.RhinoSdkMajor > rhino;
        }

        /// Плагин, у которого все .gha несовместимы с сеансом: его библиотеки в процесс не попадут.
        internal static bool IsIncompatibleGroup(PluginGroup g)
        {
            var ghas = g.Files.Where(f => f.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) && !IsInactiveTargetFolder(f))
                              .Select(ActualPath).Where(p => p != null).ToList();
            return ghas.Count > 0 && ghas.All(IsIncompatibleAssembly);
        }

        /// ".NETCoreApp,Version=v7.0" → 7; ".NETFramework,..." / ".NETStandard,..." → 0.
        static int CoreMajorFromTfm(string tfm)
        {
            if (string.IsNullOrEmpty(tfm) || !tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase)) return 0;
            int i = tfm.IndexOf("Version=v", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            var v = tfm.Substring(i + 9);
            int dot = v.IndexOf('.');
            int major;
            return int.TryParse(dot > 0 ? v.Substring(0, dot) : v, out major) ? major : 0;
        }

        /// Пакеты Rhino 8 могут содержать сборки под несколько рантаймов в папках net48, net7.0, net8.0-windows...
        /// Rhino загружает только подходящую; остальные при проверке и предзагрузке не учитываются.
        internal static bool IsInactiveTargetFolder(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir))
                {
                    var seg = Path.GetFileName(dir);
                    int tfm = TfmMajor(seg);
                    if (tfm != 0)
                    {
                        var parent = Path.GetDirectoryName(dir);
                        if (string.IsNullOrEmpty(parent)) return false;
                        var siblings = Directory.GetDirectories(parent).Select(d => TfmMajor(Path.GetFileName(d))).Where(m => m != 0).ToList();
                        if (siblings.Count < 2) return false;           // единственная папка — она и загружается
                        return tfm != ActiveTfm(siblings);
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return false;
        }

        /// "net48" → 4, "net7.0" / "net7.0-windows" → 7, "netcoreapp3.1" → 3; не папка рантайма → 0.
        static int TfmMajor(string seg)
        {
            if (string.IsNullOrEmpty(seg)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(seg, @"^net(coreapp)?(\d+)(\.\d+)?(-[a-z0-9.]+)?$",
                                                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            var digits = m.Groups[2].Value;
            if (m.Groups[1].Success && m.Groups[1].Value.Length > 0) return int.Parse(digits.Substring(0, 1));
            if (m.Groups[3].Success && m.Groups[3].Value.Length > 0) return int.Parse(digits);   // net7.0
            if (digits.Length >= 2 && digits[0] == '4') return 4;                                   // net48, net472
            return 0;
        }

        static int ActiveTfm(List<int> available)
        {
            if (Session.IsNetFramework) return available.Contains(4) ? 4 : -1;
            var core = available.Where(m => m != 4 && m <= Session.RuntimeMajor).ToList();
            if (core.Count > 0) return core.Max();
            return available.Contains(4) ? 4 : -1;
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
                // Папки с собственным .exe — это отдельные программы (например, локальный сервер, который
                // плагин запускает отдельным процессом). Их библиотеки в процесс Revit не загружаются.
                var exeDirs = new HashSet<string>(
                    FileUtil.EnumerateFiles(g.Folder, "*.exe")
                            .Select(f => Path.GetDirectoryName(f))
                            .Where(d => Directory.GetFiles(d, "*.gha").Length == 0 && Directory.GetFiles(d, "*.rhp").Length == 0),
                    StringComparer.OrdinalIgnoreCase);
                bool InExeFolder(string f)
                {
                    for (var d = Path.GetDirectoryName(f); d != null && d.Length >= g.Folder.Length; d = Path.GetDirectoryName(d))
                        if (exeDirs.Contains(d)) return true;
                    return false;
                }
                return FileUtil.EnumerateFiles(g.Folder, "*.dll")
                               .Where(f => !IsInactiveTargetFolder(f) && !InExeFolder(f))
                               .ToList();
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
                    facts.StrongNamed = !ad.PublicKey.IsNil;

                    foreach (var h in ad.GetCustomAttributes())
                    {
                        var ca = md.GetCustomAttribute(h);
                        if (AttributeTypeName(md, ca) != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
                        try
                        {
                            var br = md.GetBlobReader(ca.Value);
                            if (br.ReadUInt16() == 1) facts.TargetFramework = br.ReadSerializedString();
                            facts.CoreRuntimeMajor = CoreMajorFromTfm(facts.TargetFramework);
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

                int systemRuntimeMajor = 0;
                foreach (var h in md.AssemblyReferences)
                {
                    var ar = md.GetAssemblyReference(h);
                    var refName = md.GetString(ar.Name);
                    if (refName.StartsWith("IronPython", StringComparison.OrdinalIgnoreCase))
                        facts.UsesIronPython = true;
                    else if (refName == "RhinoCommon" || refName == "Grasshopper")
                        facts.RhinoSdkMajor = Math.Max(facts.RhinoSdkMajor, ar.Version.Major);
                    else if (refName == "System.Runtime")
                        systemRuntimeMajor = Math.Max(systemRuntimeMajor, ar.Version.Major);
                }
                // Без атрибута TargetFramework: System.Runtime 5.0+ означает .NET 5 и новее
                if (facts.TargetFramework == null && systemRuntimeMajor >= 5)
                    facts.CoreRuntimeMajor = systemRuntimeMajor;

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
