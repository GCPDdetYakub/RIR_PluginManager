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
        public string DisabledElsewhere { get; set; }             // отключён другим работающим Revit ("Revit 2027"), иначе null
    }

    public sealed class Profile
    {
        public bool AutoApply;
        public bool IconFix = true;            // восстанавливать ресурсы старых плагинов без BinaryFormatter
        public bool PreloadShared = true;      // заранее загружать новейшие версии общих библиотек плагинов
        public HashSet<string> PreloadExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string Name;                    // имя профиля (файл profiles\rhinoN\Имя.txt)
        // активные профили по версиям Rhino: "profile_rhino8" -> "Имя" (хранится в настройках)
        public Dictionary<string, string> ActiveProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        /// Версия запущенного Revit ("2021" ... "2027"), задаётся при старте надстройки.
        public static string RevitVersion { get; set; } = "unknown";
        // Настройки надстройки — общие для версии Revit; профили — в папке profiles (см. «Профили»)
        static string SettingsName => $"settings-revit{RevitVersion}.txt";
        static string SettingsPath => Path.Combine(DataDir, SettingsName);
        static string FoldersPath => Path.Combine(DataDir, "folders.txt");
        public static string LogsDir => Path.Combine(DataDir, "logs");
        const int MaxSessionLogs = 5;                        // хранятся логи только последних запусков Revit

        /// Есть ли файлы, отключённые этим процессом Revit (единый журнал, см. DisabledJournal).
        public static bool HasDisabledFiles => DisabledJournal.HasOwnEntries;

        // ---------- Где искать плагины ----------

        internal static IEnumerable<(string label, string path)> Roots()
        {
            yield return ("Libraries", Path.Combine(AppData, "Grasshopper", "Libraries"));
            // Пакеты Package Manager — своя папка у каждой версии Rhino (7.0, 8.0, 9.0)
            yield return ("Packages", Path.Combine(AppData, "McNeel", "Rhinoceros", "packages", Session.RhinoMajorOrDefault + ".0"));

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
            // Файлы, которые отключила надстройка (этот или другой процесс Revit, по единому журналу):
            // их тоже показываем как отключённые. Чужие *.off (переименованные вручную) не трогаем и не показываем.
            var tracked = DisabledJournal.ReadAll(out _);

            foreach (var (label, root) in Roots())
            {
                if (!Directory.Exists(root)) continue;

                List<string> files;
                try { files = FileUtil.EnumerateFiles(root, "*"); }
                catch (Exception ex) { Log($"Scan {root}: {ex.Message}"); continue; }

                foreach (var file in files)
                {
                    string f = file;
                    bool disabled = false;
                    if (f.EndsWith(OffSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var orig = f.Substring(0, f.Length - OffSuffix.Length);
                        if (!IsPluginFile(orig) || !tracked.ContainsKey(orig) || File.Exists(orig)) continue;
                        f = orig;
                        disabled = true;
                    }
                    else if (!IsPluginFile(f)) continue;

                    // Группа = первая папка внутри корня (для пакетов это имя пакета)
                    var rel = FileUtil.RelativePath(root, f);
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
                    if (disabled)
                    {
                        g.DisabledFiles++;
                        // Отключён только другим работающим Revit — показать, каким
                        var owners = tracked[f];
                        if (!owners.Any(o => o.Own))
                        {
                            var others = owners.Where(o => o.Alive).Select(o => "Revit " + o.Entry.Revit).Distinct().ToList();
                            if (others.Count > 0) g.DisabledElsewhere = string.Join(", ", others);
                        }
                    }
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

        /// Возвращает свои ранее отключённые файлы, затем отключает группы из профиля.
        /// Запись в единый журнал делается до переименования (см. DisabledJournal).
        public static List<string> Apply(Profile profile, out int moved)
        {
            var errors = RestoreOwn();
            moved = 0;
            if (profile.Disabled.Count == 0) return errors;

            var files = Scan().Where(g => profile.Disabled.Contains(g.Key)).SelectMany(g => g.Files).ToList();
            moved = DisabledJournal.Disable(files, errors);
            Log($"Apply: отключено файлов: {moved}, ошибок: {errors.Count}");
            return errors;
        }

        /// Возвращает имена файлам, которые отключил этот процесс Revit (после загрузки Grasshopper и при выходе).
        /// Файл, который ещё нужен другому работающему Revit, остаётся отключённым.
        public static List<string> RestoreOwn() => DisabledJournal.RestoreOwn();

        /// Свои файлы и файлы завершившихся процессов Revit (кнопка «Вернуть все файлы»).
        /// Файлы, отключённые другим работающим Revit, не трогаются.
        public static List<string> RestoreAll()
        {
            var errors = DisabledJournal.RestoreOwn();
            errors.AddRange(DisabledJournal.RestoreOrphans());
            return errors;
        }

        /// При запуске Revit: перенос старых журналов renamed.txt (до 1.18) и возврат файлов,
        /// которые остались отключёнными после сбоя любой версии Revit.
        public static List<string> RecoverOnStartup()
        {
            try
            {
                DisabledJournal.RevitVersion = RevitVersion;
                DisabledJournal.ImportLegacy(new[] { Path.Combine(LegacyDir, "renamed.txt") }, Log);
            }
            catch (Exception ex) { Log("Перенос старых журналов: " + ex.Message); }
            var errors = DisabledJournal.RestoreOrphans();
            Log($"Единый журнал: {DisabledJournal.FilePath}; восстановление после сбоя, ошибок: {errors.Count}");
            return errors;
        }

        // ---------- Профили ----------
        // Настройки надстройки (settings-revitГГГГ.txt) — общие для версии Revit.
        // Профили — именованные списки отключённых плагинов, свои для каждой версии Rhino:
        //   profiles\rhino7\<имя>.txt, profiles\rhino8\<имя>.txt ...
        // Какой профиль активен для каждой версии Rhino, записано в настройках (profile_rhino8=Имя).

        public const string DefaultProfileName = "Основной";
        public const int MaxProfileNameLength = 60;
        public static string ProfilesRoot => Path.Combine(DataDir, "profiles");
        public static string ProfilesDir => ProfilesDirFor(DataDir, Session.RhinoMajorOrDefault);
        static string ProfilesDirFor(string dataDir, int rhino) => Path.Combine(dataDir, "profiles", "rhino" + rhino);
        static string ProfileFile(string name) => Path.Combine(ProfilesDir, name + ".txt");
        static string ActiveKey => "profile_rhino" + Session.RhinoMajorOrDefault;

        /// Настройки и активный профиль текущей версии Rhino.
        public static Profile LoadProfile()
        {
            EnsureProfileFiles();
            var p = ReadSettings();
            p.Name = ResolveActiveName(p);
            ReadProfileFile(ProfileFile(p.Name), p);
            return p;
        }

        /// Имена профилей текущей версии Rhino (по алфавиту).
        public static List<string> ListProfiles()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(ProfilesDir))
                    list.AddRange(Directory.GetFiles(ProfilesDir, "*.txt").Select(f => Path.GetFileNameWithoutExtension(f)));
            }
            catch (Exception ex) { Log("ListProfiles: " + ex.Message); }
            return list.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// Отключённые плагины указанного профиля.
        public static HashSet<string> LoadDisabled(string name)
        {
            var p = new Profile();
            ReadProfileFile(ProfileFile(name), p);
            return p.Disabled;
        }

        /// Сохраняет настройки и профиль p.Name и делает его активным для текущей версии Rhino.
        public static void SaveProfile(Profile p)
        {
            if (string.IsNullOrEmpty(p.Name)) p.Name = DefaultProfileName;
            Directory.CreateDirectory(ProfilesDir);
            p.ActiveProfiles[ActiveKey] = p.Name;
            WriteSettings(p);
            WriteDisabled(p.Disabled, ProfileFile(p.Name), p.Name, Session.RhinoMajorOrDefault);
        }

        /// Записывает только список отключённых плагинов профиля (активный профиль не меняется).
        public static void SaveDisabled(string name, IEnumerable<string> disabled)
        {
            Directory.CreateDirectory(ProfilesDir);
            WriteDisabled(disabled, ProfileFile(name), name, Session.RhinoMajorOrDefault);
        }

        /// Проверка имени нового профиля. null — имя подходит, иначе текст ошибки.
        public static string ValidateProfileName(string name, string renaming = null)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Введите имя профиля.";
            if (name.Length > MaxProfileNameLength) return $"Имя длиннее {MaxProfileNameLength} символов.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "Имя не может содержать символы \\ / : * ? \" < > |";
            if (name.EndsWith(".")) return "Имя не может заканчиваться точкой.";
            var upper = name.ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(upper) ||
                ((upper.StartsWith("COM") || upper.StartsWith("LPT")) && upper.Length == 4 && char.IsDigit(upper[3])))
                return "Это имя зарезервировано Windows.";
            bool sameAsOld = renaming != null && string.Equals(name, renaming, StringComparison.OrdinalIgnoreCase);
            if (!sameAsOld && ListProfiles().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return "Профиль с таким именем уже есть.";
            return null;
        }

        public static void CreateProfile(string name, IEnumerable<string> disabled)
        {
            name = name.Trim();
            var err = ValidateProfileName(name);
            if (err != null) throw new InvalidOperationException(err);
            SaveDisabled(name, disabled);
            Log($"Профиль «{name}» создан (Rhino {Session.RhinoMajorOrDefault})");
        }

        public static void RenameProfile(string oldName, string newName)
        {
            newName = newName.Trim();
            var err = ValidateProfileName(newName, oldName);
            if (err != null) throw new InvalidOperationException(err);
            if (newName == oldName) return;

            var src = ProfileFile(oldName);
            var dst = ProfileFile(newName);
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                // Меняется только регистр букв: через временное имя
                var tmp = Path.Combine(ProfilesDir, Guid.NewGuid().ToString("N") + ".tmp");
                File.Move(src, tmp);
                File.Move(tmp, dst);
            }
            else File.Move(src, dst);

            // Заголовок файла содержит имя профиля
            WriteDisabled(LoadDisabled(newName), dst, newName, Session.RhinoMajorOrDefault);

            var s = ReadSettings();
            if (s.ActiveProfiles.TryGetValue(ActiveKey, out var active) &&
                string.Equals(active, oldName, StringComparison.OrdinalIgnoreCase))
            {
                s.ActiveProfiles[ActiveKey] = newName;
                WriteSettings(s);
            }
            Log($"Профиль «{oldName}» переименован в «{newName}» (Rhino {Session.RhinoMajorOrDefault})");
        }

        /// Удаляет профиль. Последний профиль удалить нельзя.
        public static void DeleteProfile(string name)
        {
            var all = ListProfiles();
            if (all.Count <= 1) throw new InvalidOperationException("Нельзя удалить единственный профиль.");
            File.Delete(ProfileFile(name));

            var s = ReadSettings();
            if (s.ActiveProfiles.TryGetValue(ActiveKey, out var active) &&
                string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
            {
                s.ActiveProfiles[ActiveKey] = ListProfiles().First();
                WriteSettings(s);
            }
            Log($"Профиль «{name}» удалён (Rhino {Session.RhinoMajorOrDefault})");
        }

        static Profile ReadSettings()
        {
            var p = new Profile();
            ReadProfileFile(SettingsPath, p);
            return p;
        }

        /// Активный профиль из настроек; если его файла нет — первый по алфавиту или «Основной».
        static string ResolveActiveName(Profile settings)
        {
            var all = ListProfiles();
            if (settings.ActiveProfiles.TryGetValue(ActiveKey, out var name))
            {
                var match = all.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return all.FirstOrDefault(n => n == DefaultProfileName) ?? all.FirstOrDefault() ?? DefaultProfileName;
        }

        static void ReadProfileFile(string path, Profile p)
        {
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('=');
                if (i < 0) continue;
                var k = line.Substring(0, i).Trim();
                var v = line.Substring(i + 1).Trim();

                if (k.Equals("autoapply", StringComparison.OrdinalIgnoreCase))
                    p.AutoApply = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("iconfix", StringComparison.OrdinalIgnoreCase))
                    p.IconFix = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("preloadshared", StringComparison.OrdinalIgnoreCase))
                    p.PreloadShared = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
                else if (k.Equals("preload_exclude", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                    p.PreloadExclude.Add(v);
                else if (k.StartsWith("profile_rhino", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                    p.ActiveProfiles[k.ToLowerInvariant()] = v;
                else if (k.Equals("disabled", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                    p.Disabled.Add(v);
            }
        }

        static void WriteSettings(Profile p)
        {
            Directory.CreateDirectory(DataDir);
            var lines = new List<string>
            {
                $"# Настройки RIR_PluginManager для Revit {RevitVersion}",
                "# autoapply=true      - отключать плагины из профиля автоматически при запуске Rhino",
                "# iconfix=true        - восстанавливать иконки старых плагинов (только .NET 9+); действует после перезапуска Revit",
                "# preloadshared=true  - заранее загружать новейшие версии общих библиотек плагинов; действует со следующего запуска Rhino",
                "# preload_exclude=Имя - не загружать заранее эту библиотеку (можно несколько строк)",
                "# profile_rhino8=Имя  - активный профиль для Rhino 8 (файл profiles\\rhino8\\Имя.txt)",
                "autoapply=" + (p.AutoApply ? "true" : "false"),
                "iconfix=" + (p.IconFix ? "true" : "false"),
                "preloadshared=" + (p.PreloadShared ? "true" : "false")
            };
            lines.AddRange(p.PreloadExclude.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "preload_exclude=" + x));
            lines.AddRange(p.ActiveProfiles.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value));
            File.WriteAllLines(SettingsPath, lines);
        }

        static void WriteDisabled(IEnumerable<string> disabled, string path, string name, int rhino)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = new List<string>
            {
                $"# Профиль «{name}»: плагины, которые НЕ загружаются в Rhino.Inside (Revit {RevitVersion}, Rhino {rhino})",
                "# disabled=Источник|Имя"
            };
            lines.AddRange(disabled.Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "disabled=" + x));
            File.WriteAllLines(path, lines);
        }

        /// Готовит настройки и профили для текущих версий Revit и Rhino:
        /// 1) единый профиль profile-revitГГГГ.txt (до 1.16) делится на настройки и профиль Rhino 8;
        /// 2) профили profile-revitГГГГ-rhinoN.txt (1.16) переносятся в profiles\rhinoN\Основной.txt;
        /// 3) если настроек нет — копируются настройки другой версии Revit из этой же папки;
        /// 4) если профилей текущей версии Rhino нет — копируются профили из папки надстройки
        ///    другой версии Revit (…\Addins\ГГГГ\RIR_PluginManager), иначе создаётся пустой «Основной».
        public static void EnsureProfileFiles()
        {
            try
            {
                if (!Directory.Exists(DataDir)) return;

                // 1. Единый профиль до 1.16
                var legacy = Directory.GetFiles(DataDir, "profile-revit*.txt")
                                      .Where(f => Path.GetFileName(f).IndexOf("-rhino", StringComparison.OrdinalIgnoreCase) < 0)
                                      .OrderByDescending(f => string.Equals(Path.GetFileName(f), $"profile-revit{RevitVersion}.txt", StringComparison.OrdinalIgnoreCase))
                                      .ThenByDescending(f => File.GetLastWriteTimeUtc(f))
                                      .FirstOrDefault();
                if (legacy != null && !File.Exists(SettingsPath))
                {
                    var old = new Profile();
                    ReadProfileFile(legacy, old);
                    var rhino8 = Path.Combine(ProfilesDirFor(DataDir, 8), DefaultProfileName + ".txt");
                    WriteSettings(old);
                    if (!File.Exists(rhino8)) WriteDisabled(old.Disabled, rhino8, DefaultProfileName, 8);
                    File.Delete(legacy);
                    Log($"Профиль {Path.GetFileName(legacy)} разделён на {SettingsName} и profiles\\rhino8\\{DefaultProfileName}.txt");
                }

                // 2. Профили 1.16: profile-revitГГГГ-rhinoN.txt -> profiles\rhinoN\Основной.txt
                var v116 = Directory.GetFiles(DataDir, "profile-revit*-rhino*.txt");
                foreach (var byRhino in v116.GroupBy(RhinoOfV116File).Where(gr => gr.Key > 0))
                {
                    var dir = ProfilesDirFor(DataDir, byRhino.Key);
                    var src = byRhino.OrderByDescending(f => string.Equals(Path.GetFileName(f),
                                                         $"profile-revit{RevitVersion}-rhino{byRhino.Key}.txt", StringComparison.OrdinalIgnoreCase))
                                     .ThenByDescending(f => File.GetLastWriteTimeUtc(f))
                                     .First();
                    bool empty = !Directory.Exists(dir) || Directory.GetFiles(dir, "*.txt").Length == 0;
                    if (empty)
                    {
                        var p = new Profile();
                        ReadProfileFile(src, p);
                        WriteDisabled(p.Disabled, Path.Combine(dir, DefaultProfileName + ".txt"), DefaultProfileName, byRhino.Key);
                        Log($"Профиль {Path.GetFileName(src)} перенесён в profiles\\rhino{byRhino.Key}\\{DefaultProfileName}.txt");
                    }
                    foreach (var f in byRhino) File.Delete(f);
                }

                // 3. Настройки другой версии Revit из этой же папки
                if (!File.Exists(SettingsPath))
                {
                    var other = Directory.GetFiles(DataDir, "settings-revit*.txt")
                                         .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                    if (other != null) { File.Copy(other, SettingsPath); Log($"Настройки скопированы из {Path.GetFileName(other)}"); }
                }

                // 4. Профили текущей версии Rhino
                if (ListProfiles().Count == 0)
                {
                    if (!CopyProfilesFromOtherRevit())
                    {
                        WriteDisabled(new string[0], ProfileFile(DefaultProfileName), DefaultProfileName, Session.RhinoMajorOrDefault);
                        Log($"Создан пустой профиль «{DefaultProfileName}» для Rhino {Session.RhinoMajorOrDefault}");
                    }
                }
            }
            catch (Exception ex) { Log("EnsureProfileFiles: " + ex.Message); }
        }

        static int RhinoOfV116File(string path)
        {
            var n = Path.GetFileNameWithoutExtension(path);
            int i = n.LastIndexOf("-rhino", StringComparison.OrdinalIgnoreCase);
            return i >= 0 && int.TryParse(n.Substring(i + 6), out var r) ? r : 0;
        }

        /// Копирует профили текущей версии Rhino из папки надстройки другой версии Revit
        /// (самой недавно изменённой). Понимает и прежний формат 1.16.
        static bool CopyProfilesFromOtherRevit()
        {
            int rhino = Session.RhinoMajorOrDefault;
            var addins = Path.GetDirectoryName(Path.GetDirectoryName(DataDir));   // …\Revit\Addins
            if (string.IsNullOrEmpty(addins) || !Directory.Exists(addins)) return false;

            var candidates = new List<(DateTime time, string[] files, bool v116, string from)>();
            foreach (var yearDir in Directory.GetDirectories(addins))
            {
                var other = Path.Combine(yearDir, Path.GetFileName(DataDir));
                if (string.Equals(Path.GetFullPath(other).TrimEnd('\\'), Path.GetFullPath(DataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue;
                var dir = ProfilesDirFor(other, rhino);
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.txt");
                    if (files.Length > 0)
                    {
                        candidates.Add((files.Max(f => File.GetLastWriteTimeUtc(f)), files, false, dir));
                        continue;
                    }
                }
                if (Directory.Exists(other))
                {
                    var old = Directory.GetFiles(other, $"profile-revit*-rhino{rhino}.txt");
                    if (old.Length > 0)
                    {
                        var newest = old.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                        candidates.Add((File.GetLastWriteTimeUtc(newest), new[] { newest }, true, other));
                    }
                }
            }
            if (candidates.Count == 0) return false;

            var best = candidates.OrderByDescending(c => c.time).First();
            if (best.v116)
            {
                var p = new Profile();
                ReadProfileFile(best.files[0], p);
                WriteDisabled(p.Disabled, ProfileFile(DefaultProfileName), DefaultProfileName, rhino);
            }
            else
            {
                Directory.CreateDirectory(ProfilesDir);
                foreach (var f in best.files) File.Copy(f, Path.Combine(ProfilesDir, Path.GetFileName(f)));
            }
            Log($"Профили Rhino {rhino} скопированы из {best.from}");
            return true;
        }

        // ---------- Служебное ----------

        /// Папка надстройки до переименования в RIR_PluginManager (версии до 1.11), рядом с текущей.
        static string OldAddinDir => Path.Combine(Path.GetDirectoryName(DataDir) ?? "", "RirPluginManager");

        /// Переносит профиль и список папок из прежних мест:
        /// %APPDATA%\RirPluginManager (версии 1.0–1.2) и ...\Addins\<версия>\RirPluginManager (до 1.11).
        public static void MigrateLegacy()
        {
            foreach (var dir in new[] { LegacyDir, OldAddinDir })
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    // Профиль прежнего формата копируется как есть; EnsureProfileFiles разделит его
                    if (!File.Exists(SettingsPath))
                    {
                        var prof = Directory.GetFiles(dir, "profile-revit*.txt")
                                            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                            .FirstOrDefault();
                        var dst = prof == null ? null : Path.Combine(DataDir, Path.GetFileName(prof));
                        if (prof != null && !File.Exists(dst))
                        {
                            Directory.CreateDirectory(DataDir);
                            File.Copy(prof, dst);
                            Log($"Migrate: профиль {Path.GetFileName(prof)} из {dir}");
                        }
                    }

                    // Папки пользователя копируем. Старые списки отключённых файлов (renamed.txt)
                    // переносит в единый журнал DisabledJournal.ImportLegacy.
                    foreach (var name in new[] { "folders.txt" })
                    {
                        var src = Path.Combine(dir, name);
                        if (!File.Exists(src)) continue;
                        var dst = Path.Combine(DataDir, name);
                        if (!File.Exists(dst)) File.Copy(src, dst);
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
