using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace RIR_PluginManager
{
    /// Запись единого журнала: один файл, отключённый одним процессом Revit.
    public sealed class JournalEntry
    {
        public string Path;            // исходный путь файла (без .off)
        public string Revit;           // год Revit, "?" — неизвестен (перенесено из старого журнала)
        public int Pid;                // 0 — владельца нет (старый журнал, конфликт или ошибка возврата)
        public DateTime StartUtc;      // время запуска процесса-владельца (номера процессов Windows переиспользует)
        public string Machine;         // компьютер (AppData\Roaming может «переезжать» между компьютерами)
        public DateTime WrittenUtc;

        const char Sep = '\t';

        public string Format() => string.Join(Sep.ToString(), new[]
        {
            Path, Revit ?? "?", Pid.ToString(CultureInfo.InvariantCulture),
            StartUtc.ToString("o", CultureInfo.InvariantCulture), Machine ?? "",
            WrittenUtc.ToString("o", CultureInfo.InvariantCulture)
        });

        public static JournalEntry Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) return null;
            var p = line.Split(Sep);
            var e = new JournalEntry { Path = p[0].Trim(), Revit = "?", Machine = "" };
            if (e.Path.Length == 0) return null;
            if (p.Length > 1 && p[1].Trim().Length > 0) e.Revit = p[1].Trim();
            if (p.Length > 2) int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Pid);
            if (p.Length > 3) DateTime.TryParse(p[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out e.StartUtc);
            if (p.Length > 4) e.Machine = p[4].Trim();
            if (p.Length > 5) DateTime.TryParse(p[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out e.WrittenUtc);
            return e;
        }
    }

    /// Единый журнал отключённых файлов для всех версий Revit:
    /// %APPDATA%\Autodesk\Revit\Addins\RIR_PluginManager\disabled-plugins.txt
    ///
    /// Каждая запись знает процесс Revit, который отключил файл. Правила:
    ///  - процесс возвращает только свои файлы; имя возвращается, когда на файл не осталось живых записей;
    ///  - записи завершившихся процессов («осиротевшие») возвращает любой Revit при запуске и restore.ps1;
    ///  - записи работающих процессов не трогает никто.
    /// Все операции с журналом и переименования — под именованной блокировкой (её же берёт restore.ps1).
    public static class DisabledJournal
    {
        public const string FileName = "disabled-plugins.txt";
        public const string MutexName = @"Local\RIR_PluginManager.DisabledPlugins";
        const string OffSuffix = ".off";
        static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
        static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

        static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string AddinsRoot => System.IO.Path.Combine(AppData, "Autodesk", "Revit", "Addins");
        public static string Dir => System.IO.Path.Combine(AddinsRoot, "RIR_PluginManager");
        public static string FilePath => System.IO.Path.Combine(Dir, FileName);

        static readonly int OwnPid;
        static readonly DateTime OwnStartUtc;
        static readonly string Machine = Environment.MachineName;

        /// Есть ли файлы, отключённые этим процессом (без чтения журнала — для проверки по Idling).
        public static bool HasOwnEntries { get; private set; }

        /// Год запущенного Revit ("2024"); задаётся при старте надстройки.
        public static string RevitVersion = "?";

        static DisabledJournal()
        {
            using (var me = Process.GetCurrentProcess())
            {
                OwnPid = me.Id;
                try { OwnStartUtc = me.StartTime.ToUniversalTime(); } catch { OwnStartUtc = DateTime.MinValue; }
            }
        }

        // ---------- Блокировка, чтение, запись ----------

        static T Locked<T>(Func<List<JournalEntry>, T> action, out bool lockFailed)
        {
            lockFailed = false;
            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned;
                try { owned = mutex.WaitOne(LockTimeout); }
                catch (AbandonedMutexException) { owned = true; }   // прежний владелец завершился, не освободив блокировку
                if (!owned) { lockFailed = true; return default(T); }
                try
                {
                    var entries = Read();
                    var result = action(entries);
                    if (Snapshot(entries) != _onDisk) Write(entries);   // записать, только если что-то изменилось
                    return result;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static string Snapshot(List<JournalEntry> entries) => string.Join("\n", entries.Select(e => e.Format()));
        static string _onDisk = "";                                  // содержимое журнала на диске (под блокировкой)

        static List<JournalEntry> Read()
        {
            var list = new List<JournalEntry>();
            if (File.Exists(FilePath))
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var e = JournalEntry.Parse(line);
                    if (e != null) list.Add(e);
                }
            }
            _onDisk = Snapshot(list);
            return list;
        }

        static void Write(List<JournalEntry> entries)
        {
            _onDisk = Snapshot(entries);
            if (entries.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Dir);
            var lines = new List<string>
            {
                "# RIR_PluginManager: plugin files disabled (renamed to *.off) by Revit processes.",
                "# path<TAB>Revit<TAB>pid<TAB>process start (UTC)<TAB>computer<TAB>written (UTC). Do not edit while Revit is running."
            };
            lines.AddRange(entries.Select(e => e.Format()));
            // Через временный файл: при сбое записи прежний журнал не теряется
            var tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, lines);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        // ---------- Живые и осиротевшие записи ----------

        static bool IsOwn(JournalEntry e) =>
            e.Pid == OwnPid && SameStart(e.StartUtc, OwnStartUtc) && SameMachine(e);

        static bool SameMachine(JournalEntry e) => string.Equals(e.Machine, Machine, StringComparison.OrdinalIgnoreCase);
        static bool SameStart(DateTime a, DateTime b) => (a - b).Duration() <= StartTolerance;

        /// Жив ли процесс-владелец записи. Если проверить нельзя (нет доступа к процессу) — считается живым.
        static bool IsAlive(JournalEntry e, Dictionary<int, DateTime?> cache)
        {
            if (e.Pid <= 0 || !SameMachine(e)) return false;
            if (IsOwn(e)) return true;
            if (!cache.TryGetValue(e.Pid, out var start))
            {
                try
                {
                    using (var p = Process.GetProcessById(e.Pid))
                    {
                        try { start = p.StartTime.ToUniversalTime(); }
                        catch { start = e.StartUtc; }              // нет доступа — не трогаем
                    }
                }
                catch (ArgumentException) { start = null; }     // процесса нет
                catch { start = e.StartUtc; }
                cache[e.Pid] = start;
            }
            return start.HasValue && SameStart(start.Value, e.StartUtc);
        }

        static JournalEntry Own(string path) => new JournalEntry
        {
            Path = path, Revit = RevitVersion, Pid = OwnPid, StartUtc = OwnStartUtc,
            Machine = Machine, WrittenUtc = DateTime.UtcNow
        };

        static string Key(string path) => path.ToUpperInvariant();

        // ---------- Операции ----------

        /// Отключить файлы: сначала запись, потом переименование. Файл, уже отключённый другим
        /// работающим Revit, не трогается — добавляется только своя запись.
        public static int Disable(IEnumerable<string> files, List<string> errors)
        {
            int moved = Locked(entries =>
            {
                var cache = new Dictionary<int, DateTime?>();
                int n = 0;
                foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var off = f + OffSuffix;
                    bool mine = entries.Any(e => IsOwn(e) && Key(e.Path) == Key(f));
                    if (File.Exists(f))
                    {
                        if (File.Exists(off))
                        {
                            errors.Add(L.T($"{f}: уже существует {System.IO.Path.GetFileName(off)}, файл не тронут",
                                            $"{f}: {System.IO.Path.GetFileName(off)} already exists, file left untouched"));
                            continue;
                        }
                        var entry = Own(f);
                        if (!mine) entries.Add(entry);
                        Write(entries);            // запись на диске до переименования
                        try { File.Move(f, off); n++; HasOwnEntries = true; }
                        catch (Exception ex)
                        {
                            errors.Add($"{f}: {ex.Message}");
                            if (!mine) entries.Remove(entry);
                        }
                    }
                    else if (File.Exists(off) && !mine &&
                             entries.Any(e => Key(e.Path) == Key(f) && !IsOwn(e) && IsAlive(e, cache)))
                    {
                        entries.Add(Own(f));       // файл уже отключён другим работающим Revit
                        HasOwnEntries = true;
                    }
                }
                return n;
            }, out var lockFailed);
            if (lockFailed) errors.Add(L.T("Журнал отключённых файлов занят другим процессом, плагины не отключены",
                                           "The list of disabled files is locked by another process, plugins were not disabled"));
            return moved;
        }

        /// Вернуть имена файлам этого процесса. Файл, который ещё нужен другому работающему Revit,
        /// остаётся отключённым. Не удалось вернуть (конфликт, ошибка) — запись становится осиротевшей,
        /// её повторит следующий запуск Revit или restore.ps1.
        public static List<string> RestoreOwn()
        {
            var errors = new List<string>();
            Locked(entries =>
            {
                var own = entries.Where(IsOwn).ToList();
                if (own.Count == 0) return 0;
                foreach (var e in own) entries.Remove(e);
                var cache = new Dictionary<int, DateTime?>();
                foreach (var e in own.GroupBy(x => Key(x.Path)).Select(g => g.First()))
                {
                    if (entries.Any(x => Key(x.Path) == Key(e.Path) && IsAlive(x, cache))) continue;
                    var orphan = TryRestore(e, errors);
                    if (orphan != null) entries.Add(orphan);
                }
                return 0;
            }, out var lockFailed);
            if (lockFailed) errors.Add(L.T("Журнал отключённых файлов занят другим процессом, имена не возвращены (повторится позже)",
                                           "The list of disabled files is locked by another process, names were not restored (will retry later)"));
            else HasOwnEntries = false;
            return errors;
        }

        /// Вернуть имена файлам завершившихся процессов (после сбоя любой версии Revit).
        public static List<string> RestoreOrphans()
        {
            var errors = new List<string>();
            Locked(entries =>
            {
                var cache = new Dictionary<int, DateTime?>();
                var orphans = entries.Where(e => !IsAlive(e, cache)).ToList();
                foreach (var e in orphans) entries.Remove(e);
                foreach (var e in orphans.GroupBy(x => Key(x.Path)).Select(g => g.First()))
                {
                    if (entries.Any(x => Key(x.Path) == Key(e.Path))) continue;   // файл нужен работающему Revit
                    var left = TryRestore(e, errors);
                    if (left != null) entries.Add(left);
                }
                return 0;
            }, out var lockFailed);
            if (lockFailed) errors.Add(L.T("Журнал отключённых файлов занят другим процессом, восстановление после сбоя пропущено",
                                           "The list of disabled files is locked by another process, recovery after a crash skipped"));
            return errors;
        }

        /// Вернуть имя файлу. null — запись больше не нужна; иначе — осиротевшая запись для повтора.
        static JournalEntry TryRestore(JournalEntry e, List<string> errors)
        {
            var off = e.Path + OffSuffix;
            if (!File.Exists(off)) return null;                    // уже возвращён или не был переименован
            var left = new JournalEntry
            {
                Path = e.Path, Revit = e.Revit, Pid = 0, StartUtc = DateTime.MinValue,
                Machine = e.Machine, WrittenUtc = DateTime.UtcNow
            };
            if (File.Exists(e.Path))
            {
                errors.Add(L.T($"{e.Path}: оригинал уже существует, {System.IO.Path.GetFileName(off)} оставлен как есть",
                                $"{e.Path}: the original already exists, {System.IO.Path.GetFileName(off)} left as is"));
                return left;
            }
            try { File.Move(off, e.Path); return null; }
            catch (Exception ex)
            {
                errors.Add(L.T($"{e.Path}: не удалось восстановить: {ex.Message}", $"{e.Path}: could not restore: {ex.Message}"));
                return left;
            }
        }

        /// Все записи журнала по файлам: для окна (кто отключил файл).
        public static Dictionary<string, List<Owner>> ReadAll(out bool lockFailed)
        {
            return Locked(entries =>
            {
                var cache = new Dictionary<int, DateTime?>();
                var map = new Dictionary<string, List<Owner>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    if (!map.TryGetValue(e.Path, out var list)) map[e.Path] = list = new List<Owner>();
                    list.Add(new Owner { Entry = e, Own = IsOwn(e), Alive = IsAlive(e, cache) });
                }
                return map;
            }, out lockFailed) ?? new Dictionary<string, List<Owner>>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class Owner
        {
            public JournalEntry Entry;
            public bool Own;      // отключил этот процесс
            public bool Alive;    // процесс-владелец работает
        }

        // ---------- Переход со старых журналов (renamed.txt у каждой версии Revit, до 1.18) ----------

        /// Переносит старые renamed.txt в единый журнал как осиротевшие записи.
        /// Журнал версии Revit, которая сейчас запущена другим процессом, не трогается: там может
        /// работать прежняя версия надстройки, которая ждёт загрузки Grasshopper.
        public static int ImportLegacy(IEnumerable<string> extraFiles, Action<string> log)
        {
            var running = RunningRevitYears();
            var sources = new List<(string file, string year)>();
            if (Directory.Exists(AddinsRoot))
            {
                foreach (var yearDir in Directory.GetDirectories(AddinsRoot))
                {
                    var year = System.IO.Path.GetFileName(yearDir);
                    if (!Regex.IsMatch(year, @"^\d{4}$")) continue;
                    foreach (var name in new[] { "RIR_PluginManager", "RirPluginManager" })
                        sources.Add((System.IO.Path.Combine(yearDir, name, "renamed.txt"), year));
                }
            }
            foreach (var f in extraFiles) sources.Add((f, "?"));

            int imported = 0;
            foreach (var (file, year) in sources)
            {
                if (!File.Exists(file)) continue;
                bool busy = year == RevitVersion ? false                         // свой год: прежняя версия здесь не запущена
                          : running.Contains("?") || running.Contains(year) || (year == "?" && running.Count > 0);
                if (busy) { log(L.T($"Старый журнал {file} не перенесён: запущен Revit {year}", $"Old journal {file} not migrated: Revit {year} is running")); continue; }

                Locked(entries =>
                {
                    foreach (var line in File.ReadAllLines(file))
                    {
                        var p = line.Trim();
                        if (p.Length == 0 || p.StartsWith("#")) continue;
                        if (entries.Any(e => e.Pid == 0 && Key(e.Path) == Key(p))) continue;
                        entries.Add(new JournalEntry
                        {
                            Path = p, Revit = year, Pid = 0, StartUtc = DateTime.MinValue,
                            Machine = Machine, WrittenUtc = DateTime.UtcNow
                        });
                        imported++;
                    }
                    return 0;
                }, out var lockFailed);
                if (lockFailed) { log(L.T($"Старый журнал {file} не перенесён: журнал занят", $"Old journal {file} not migrated: the list is locked")); continue; }
                try { File.Delete(file); } catch (Exception ex) { log(L.T($"Старый журнал {file} не удалён: {ex.Message}", $"Old journal {file} not deleted: {ex.Message}")); }
                log(L.T($"Старый журнал {file} перенесён в {FileName}", $"Old journal {file} migrated to {FileName}"));
            }
            return imported;
        }

        /// Годы запущенных Revit (кроме этого процесса). "?" — год не удалось определить.
        static HashSet<string> RunningRevitYears()
        {
            var years = new HashSet<string>();
            foreach (var p in Process.GetProcessesByName("Revit"))
            {
                using (p)
                {
                    if (p.Id == OwnPid) continue;
                    try
                    {
                        var m = Regex.Match(p.MainModule?.FileName ?? "", @"Revit (\d{4})", RegexOptions.IgnoreCase);
                        years.Add(m.Success ? m.Groups[1].Value : "?");
                    }
                    catch { years.Add("?"); }
                }
            }
            return years;
        }
    }
}
