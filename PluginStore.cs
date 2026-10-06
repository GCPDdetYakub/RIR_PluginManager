using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RIR_PluginManager
{
    public sealed class PluginGroup
    {
        public string Key { get; init; }      // "Источник|ИмяПапки", хранится в профиле
        public string Name { get; init; }
        public string Source { get; init; }
        public string Folder { get; init; }                        // папка плагина (null, если это одиночный файл в корне)
        public List<string> Files { get; } = new List<string>();   // исходные пути (без .off)
        public int DisabledFiles { get; set; }                    // сколько из них сейчас переименовано в .off
    }

    public sealed class Profile
    {
        public bool AutoApply;
        public bool IconFix = true;            // восстанавливать ресурсы старых плагинов без BinaryFormatter
        public bool PreloadShared = true;      // заранее загружать новейшие версии общих библиотек плагинов
        public HashSet<string> PreloadExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static class PluginStore
    {
        public const string OffSuffix = ".off";
        static readonly string[] Extensions = { ".gha", ".ghpy" };

        // Стандартные библиотеки компонентов GH: если они загружены, Grasshopper уже стартовал.
        static readonly string[] GhCoreLibs = { "MathComponents", "CurveComponents", "SurfaceComponents", "VectorComponents" };

        static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // Данные хранятся рядом с DLL: %APPDATA%\Autodesk\Revit\Addins\2027\RIR_PluginManager
        public static string DataDir => Path.GetDirectoryName(typeof(PluginStore).Assembly.Location);
        static string LegacyDir => Path.Combine(AppData, "RirPluginManager");   // версии 1.0–1.2
        /// Версия запущенного Revit ("2026", "2027"...), задаётся при старте надстройки.
        public static string RevitVersion { get; set; } = "unknown";
        static string ProfileName => $"profile-revit{RevitVersion}.txt";
        static string ProfilePath => Path.Combine(DataDir, ProfileName);
        static string StatePath => Path.Combine(DataDir, "renamed.txt");
        static string FoldersPath => Path.Combine(DataDir, "folders.txt");
        public static string LogsDir => Path.Combine(DataDir, "logs");
        const int MaxSessionLogs = 5;                        // хранятся логи только последних запусков Revit

        public static bool HasDisabledFiles => File.Exists(StatePath);

        // ---------- Где искать плагины ----------

        internal static IEnumerable<(string label, string path)> Roots()
        {
            yield return ("Libraries", Path.Combine(AppData, "Grasshopper", "Libraries"));
            yield return ("Packages", Path.Combine(AppData, "McNeel", "Rhinoceros", "packages", "8.0"));

            // Дополнительные папки (например, из GrasshopperDeveloperSettings), по одной на строку
            if (File.Exists(FoldersPath))
            {
                foreach (var raw in File.ReadAllLines(FoldersPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var path = Environment.ExpandEnvironmentVariables(line);
                    yield return ("Extra:" + Path.GetFileName(path.TrimEnd('\\', '/')), path);
                }
            }
        }

        public static List<PluginGroup> Scan()
        {
            var groups = new Dictionary<string, PluginGroup>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

            // Файлы, которые отключил этот аддон: их тоже показываем (как отключённые).
            // Чужие *.off (переименованные вручную) не трогаем и не показываем.
            var tracked = ReadState();

            foreach (var (label, root) in Roots())
            {
                if (!Directory.Exists(root)) continue;

                List<string> files;
                try { files = Directory.EnumerateFiles(root, "*", options).ToList(); }
                catch (Exception ex) { Log($"Scan {root}: {ex.Message}"); continue; }

                foreach (var file in files)
                {
                    string f = file;
                    bool disabled = false;
                    if (f.EndsWith(OffSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var orig = f.Substring(0, f.Length - OffSuffix.Length);
                        if (!IsPluginFile(orig) || !tracked.Contains(orig) || File.Exists(orig)) continue;
                        f = orig;
                        disabled = true;
                    }
                    else if (!IsPluginFile(f)) continue;

                    // Группа = первая папка внутри корня (для пакетов это имя пакета)
                    var rel = Path.GetRelativePath(root, f);
                    var first = rel.Split(Path.DirectorySeparatorChar)[0];
                    bool rootFile = first == rel;
                    var seg = rootFile ? Path.GetFileNameWithoutExtension(first) : first;

                    var key = label + "|" + seg;
                    if (!groups.TryGetValue(key, out var g))
                    {
                        g = new PluginGroup { Key = key, Name = seg, Source = label,
                                              Folder = rootFile ? null : Path.Combine(root, first) };
                        groups[key] = g;
                    }
                    g.Files.Add(f);
                    if (disabled) g.DisabledFiles++;
                }
            }

            return groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// DLL, лежащие прямо в корне Libraries (общие зависимости нескольких плагинов).
        internal static List<string> RootLibraryDlls()
        {
            var list = new List<string>();
            foreach (var (label, root) in Roots())
            {
                if (label != "Libraries" || !Directory.Exists(root)) continue;
                try { list.AddRange(Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)); }
                catch { }
            }
            return list;
        }

        static bool IsPluginFile(string f) =>
            Extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        // ---------- Отключение / восстановление ----------

        /// Возвращает все ранее переименованные файлы, затем отключает группы из профиля.
        public static List<string> Apply(Profile profile, out int moved)
        {
            var errors = RestoreAll();
            moved = 0;
            if (profile.Disabled.Count == 0) return errors;

            Directory.CreateDirectory(DataDir);
            foreach (var g in Scan().Where(g => profile.Disabled.Contains(g.Key)))
            {
                foreach (var f in g.Files)
                {
                    if (!File.Exists(f)) continue;   // не удалось восстановить ранее, уже в списке ошибок
                    var off = f + OffSuffix;
                    if (File.Exists(off))
                    {
                        errors.Add($"{f}: уже существует {Path.GetFileName(off)}, файл не тронут");
                        continue;
                    }
                    try
                    {
                        // Сначала записываем в журнал, потом переименовываем:
                        // при сбое между этими шагами восстановление просто пропустит строку.
                        File.AppendAllLines(StatePath, new[] { f });
                        File.Move(f, off);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{f}: {ex.Message}");
                    }
                }
            }
            Log($"Apply: отключено файлов: {moved}, ошибок: {errors.Count}");
            return errors;
        }

        /// Возвращает исходные имена всем файлам, которые переименовал этот аддон.
        public static List<string> RestoreAll()
        {
            var errors = new List<string>();
            if (!File.Exists(StatePath)) return errors;

            var remaining = new List<string>();
            foreach (var p in ReadState())
            {
                var off = p + OffSuffix;
                if (!File.Exists(off)) continue;              // уже восстановлен или не был переименован
                if (File.Exists(p))
                {
                    errors.Add($"{p}: оригинал уже существует, {Path.GetFileName(off)} оставлен как есть");
                    continue;
                }
                try { File.Move(off, p); }
                catch (Exception ex)
                {
                    errors.Add($"{p}: не удалось восстановить: {ex.Message}");
                    remaining.Add(p);
                }
            }

            if (remaining.Count == 0) File.Delete(StatePath);
            else File.WriteAllLines(StatePath, remaining);
            return errors;
        }

        static HashSet<string> ReadState()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(StatePath)) return set;
            foreach (var l in File.ReadAllLines(StatePath))
            {
                var t = l.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        // ---------- Профиль (текстовый файл) ----------

        public static Profile LoadProfile()
        {
            var p = new Profile();
            if (!File.Exists(ProfilePath)) return p;

            foreach (var raw in File.ReadAllLines(ProfilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('=');
                if (i < 0) continue;
                var k = line[..i].Trim();
                var v = line[(i + 1)..].Trim();

                if (k.Equals("autoapply", StringComparison.OrdinalIgnoreCase))
                    p.AutoApply = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("iconfix", StringComparison.OrdinalIgnoreCase))
                    p.IconFix = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("preloadshared", StringComparison.OrdinalIgnoreCase))
                    p.PreloadShared = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("preload_exclude", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                    p.PreloadExclude.Add(v);
                else if (k.Equals("disabled", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                    p.Disabled.Add(v);
            }
            return p;
        }

        public static void SaveProfile(Profile p)
        {
            Directory.CreateDirectory(DataDir);
            var lines = new List<string>
            {
                $"# Профиль RIR_PluginManager для Revit {RevitVersion}",
                "# autoapply=true  - отключать плагины из списка автоматически при запуске Revit",
                "# iconfix=true    - восстанавливать иконки старых плагинов (формат BinaryFormatter); действует после перезапуска Revit",
                "# disabled=Источник|Имя  - плагины, которые НЕ загружаются в Rhino.Inside",
                "autoapply=" + (p.AutoApply ? "true" : "false"),
                "# preloadshared=true - заранее загружать новейшие версии общих библиотек плагинов; действует со следующего запуска Rhino",
                "# preload_exclude=ИмяБиблиотеки - не загружать заранее эту библиотеку (можно несколько строк)",
                "iconfix=" + (p.IconFix ? "true" : "false"),
                "preloadshared=" + (p.PreloadShared ? "true" : "false")
            };
            lines.AddRange(p.PreloadExclude.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "preload_exclude=" + x));
            lines.AddRange(p.Disabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "disabled=" + x));
            File.WriteAllLines(ProfilePath, lines);
        }

        // ---------- Служебное ----------

        /// Переносит профиль и список переименований из старой папки (%APPDATA%\RirPluginManager).
        /// Если профиля для текущей версии Revit нет, а в папке надстройки лежит профиль
        /// с другим номером версии (например, скопированный из папки 2027 в 2026),
        /// он переименовывается под текущую версию.
        public static void EnsureProfileName()
        {
            try
            {
                if (File.Exists(ProfilePath) || !Directory.Exists(DataDir)) return;
                var other = Directory.GetFiles(DataDir, "profile-revit*.txt")
                                     .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                     .FirstOrDefault();
                if (other == null) return;
                File.Move(other, ProfilePath);
                Log($"Профиль {Path.GetFileName(other)} переименован в {ProfileName}");
            }
            catch (Exception ex) { Log("EnsureProfileName: " + ex.Message); }
        }

        /// Папка надстройки до переименования в RIR_PluginManager (версии до 1.11), рядом с текущей.
        static string OldAddinDir => Path.Combine(Path.GetDirectoryName(DataDir) ?? "", "RirPluginManager");

        /// Переносит профиль и список отключённых файлов из прежних мест:
        /// %APPDATA%\RirPluginManager (версии 1.0–1.2) и ...\Addins\<версия>\RirPluginManager (до 1.11).
        public static void MigrateLegacy()
        {
            foreach (var dir in new[] { LegacyDir, OldAddinDir })
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    // Профиль: самый свежий profile-revit*.txt, если у текущей версии профиля ещё нет
                    if (!File.Exists(ProfilePath))
                    {
                        var prof = Directory.GetFiles(dir, "profile-revit*.txt")
                                            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                            .FirstOrDefault();
                        if (prof != null)
                        {
                            File.Copy(prof, ProfilePath);
                            Log($"Migrate: профиль {Path.GetFileName(prof)} из {dir}");
                        }
                    }

                    // Список отключённых файлов объединяем, папки пользователя копируем
                    foreach (var name in new[] { "renamed.txt", "folders.txt" })
                    {
                        var src = Path.Combine(dir, name);
                        if (!File.Exists(src)) continue;
                        var dst = Path.Combine(DataDir, name);
                        if (name == "renamed.txt" && File.Exists(dst))
                            File.AppendAllLines(dst, File.ReadAllLines(src));
                        else if (!File.Exists(dst))
                            File.Copy(src, dst);
                        File.Delete(src);
                        Log($"Migrate: {name} из {dir}");
                    }
                }
                catch (Exception ex) { Log($"Migrate {dir}: {ex.Message}"); }
            }
        }

        public static bool GrasshopperPluginsLoaded()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = a.GetName().Name;
                    if (GhCoreLibs.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
                    if (!a.IsDynamic && a.Location.EndsWith(".gha", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { /* некоторые сборки не отдают имя или путь */ }
            }
            return false;
        }

        const long MaxLogBytes = 20L * 1024 * 1024;       // предел одного лога; дальше запись прекращается
        static readonly object _logLock = new object();
        static string _sessionLog;
        static bool _logFull;

        /// Лог текущего запуска Revit: logs\log_ГГГГ-ММ-ДД_чч-мм-сс.txt.
        /// При создании нового лога самые старые удаляются, остаются последние MaxSessionLogs.
        public static string LogFile
        {
            get
            {
                lock (_logLock)
                {
                    if (_sessionLog == null) StartSessionLog();
                    return _sessionLog;
                }
            }
        }

        static void StartSessionLog()
        {
            Directory.CreateDirectory(LogsDir);

            // Общий log.txt прежних версий переносится в папку logs и дальше удаляется по общему правилу
            try
            {
                var oldLog = Path.Combine(DataDir, "log.txt");
                if (File.Exists(oldLog))
                    File.Move(oldLog, Path.Combine(LogsDir, $"log_{File.GetLastWriteTime(oldLog):yyyy-MM-dd_HH-mm-ss}_old.txt"));
                var oldLog2 = Path.Combine(DataDir, "log.old.txt");
                if (File.Exists(oldLog2)) File.Delete(oldLog2);
            }
            catch { }

            var name = $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var path = Path.Combine(LogsDir, name + ".txt");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(LogsDir, $"{name}_{i}.txt");

            // Оставить место для нового лога: удалить самые старые сверх лимита
            try
            {
                var existing = Directory.GetFiles(LogsDir, "log_*.txt")
                                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                        .ToList();
                foreach (var f in existing.Skip(MaxSessionLogs - 1))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }

            File.WriteAllText(path, "");
            _sessionLog = path;
        }

        public static void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    if (_sessionLog == null) StartSessionLog();
                    if (_logFull) return;
                    var fi = new FileInfo(_sessionLog);
                    if (fi.Exists && fi.Length > MaxLogBytes)
                    {
                        _logFull = true;
                        File.AppendAllText(_sessionLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  Лог достиг 20 МБ, запись остановлена до следующего запуска Revit{Environment.NewLine}");
                        return;
                    }
                    File.AppendAllText(_sessionLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
