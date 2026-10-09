using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RIR_PluginManager
{
    /// «Чёрный ящик» загрузки Grasshopper (ПР-01) и список плагинов, на которых падал Revit (ПР-05).
    ///
    /// Пока Grasshopper загружается, каждая загруженная .gha дописывается в файл сессии
    /// gh-session-<pid>.txt (сразу на диск, поэтому запись переживает падение процесса).
    /// С начала загрузки и до WatchAfterLoad после неё туда же пишутся ошибки (исключения), в стеке
    /// которых есть код из папок плагинов: строка exc с файлом сборки ближайшего к ошибке кадра.
    /// После загрузки в файл пишется отметка «loaded», по окончании наблюдения — «watch-end»,
    /// при штатном закрытии Revit файл удаляется.
    /// Если при следующем запуске файл остался, а его процесс уже не работает, значит прошлый
    /// запуск завершился аварийно. Подозреваемый определяется по последней записи:
    ///   exc — плагин, в коде которого была последняя ошибка (на любом этапе до watch-end;
    ///         отметка «loaded» после неё не в счёт: процесс мог ещё секунды завершаться после ошибки);
    ///   gha без отметки «loaded» — последний загружавшийся плагин;
    ///   иначе, если упал во время загрузки, но последний файл вне папок плагинов, — точный виновник
    ///   неизвестен, и в сообщении перечисляются загруженные плагины с ошибками проверки (✖).
    /// Подозреваемый попадает в crash-suspects.txt: перед запуском Grasshopper надстройка
    /// предупреждает, если такой плагин включён. Подозрение снимается, когда сессия с этим плагином
    /// прошла загрузку и Revit закрылся штатно.
    ///
    /// Файлы лежат в папке надстройки своей версии Revit: плагины и профили у каждой версии свои.
    public static class CrashMarker
    {
        const string SessionPrefix = "gh-session-";
        const string SuspectsFileName = "crash-suspects.txt";
        const string LastCrashFileName = "last-crash.txt";
        static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
        // Сколько ещё записывать ошибки плагинов после отметки «loaded»: открытие окна Grasshopper,
        // обработчики CanvasCreated и т. п. Позже падение уже не связывается с загрузкой.
        static readonly TimeSpan WatchAfterLoad = TimeSpan.FromSeconds(60);
        const int MaxExcLines = 300;

        static string SessionPath => Path.Combine(PluginStore.DataDir, $"{SessionPrefix}{OwnPid}.txt");
        static string SuspectsPath => Path.Combine(PluginStore.DataDir, SuspectsFileName);

        static readonly object _lock = new object();
        static readonly int OwnPid;
        static readonly DateTime OwnStartUtc;
        static bool _active;              // идёт запись сессии Grasshopper
        static bool _loadedMarked;        // отметка «загрузка завершена» записана
        static DateTime _loadedAtUtc;
        static bool _watchEnded;          // запись ошибок плагинов закончена
        static int _rhino;
        static readonly List<(string path, string name)> _loaded = new List<(string, string)>();

        // Запись ошибок плагинов
        static readonly Assembly Self = typeof(CrashMarker).Assembly;
        static List<string> _rootsFull;                                       // папки плагинов, полные пути с «\» на конце
        static Dictionary<string, string> _ghaByName;                         // для сборок, загруженных из памяти
        static readonly Dictionary<Assembly, string> _asmPlugin = new Dictionary<Assembly, string>();  // "" — не плагин
        static int _excCount;
        // Отметка «loaded» по таймеру: окно Grasshopper создано и QuietAfterLoad не загружался ни один плагин
        static readonly TimeSpan QuietAfterLoad = TimeSpan.FromSeconds(5);
        static System.Threading.Timer _loadTimer;
        static DateTime _lastPluginLoadUtc;
        static PropertyInfo _ghDocumentEditor;                                // Grasshopper.Instances.DocumentEditor
        static string _lastExcSig;                                            // повтор той же ошибки подряд не пишется
        [ThreadStatic] static bool _inNote;

        /// Сообщение о падении прошлого запуска, ждёт показа (при первом Idling).
        public static CrashReport Pending { get; private set; }

        static CrashMarker()
        {
            using (var me = Process.GetCurrentProcess())
            {
                OwnPid = me.Id;
                try { OwnStartUtc = me.StartTime.ToUniversalTime(); } catch { OwnStartUtc = DateTime.MinValue; }
            }
        }

        public sealed class Suspect
        {
            public string Key;        // ключ группы в профиле ("Packages|WiresRenderer")
            public string Name;       // имя плагина для сообщений
            public string File;       // путь к .gha
            public string Assembly;   // имя сборки (для загрузки из памяти)
            public int Rhino;
            public DateTime WhenUtc;  // когда упал Revit (время начала той сессии)

            public string Format() => string.Join("\t", new[]
            {
                Key, Name, File ?? "", Assembly ?? "", Rhino.ToString(CultureInfo.InvariantCulture),
                WhenUtc.ToString("o", CultureInfo.InvariantCulture)
            });

            public static Suspect Parse(string line)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) return null;
                var p = line.Split('\t');
                if (p.Length < 6) return null;
                var s = new Suspect { Key = p[0], Name = p[1], File = p[2], Assembly = p[3] };
                int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out s.Rhino);
                DateTime.TryParse(p[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out s.WhenUtc);
                return s;
            }
        }

        public enum CrashKind
        {
            LastLoaded,     // упал во время загрузки, подозреваемый — последний загружавшийся плагин
            PluginError,    // последней записью была ошибка в коде плагина
            Unknown         // виновник не определён; Candidates — загруженные плагины с ✖
        }

        public sealed class CrashReport
        {
            public CrashKind Kind;
            public bool AfterLoad;       // упал после отметки «loaded»
            public string File;          // файл подозреваемого (.gha или его библиотека); для Unknown — последний загружавшийся
            public string Assembly;
            public string Key;           // null — нет подозреваемого среди плагинов
            public string Name;
            public string Error;         // текст ошибки (PluginError)
            public List<(string key, string name)> Candidates = new List<(string, string)>();
            public int Rhino;
            public string ProfilePath;   // активный профиль той сессии
            public DateTime WhenUtc;
        }

        // ---------- Запуск Revit ----------

        /// Разобрать сессии прошлых запусков и начать следить за загрузкой сборок.
        public static void Arm()
        {
            try { CheckPrevious(); }
            catch (Exception ex) { PluginStore.Log("Маркер падения: " + ex.Message); }
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        static void CheckPrevious()
        {
            if (!Directory.Exists(PluginStore.DataDir)) return;
            foreach (var file in Directory.GetFiles(PluginStore.DataDir, SessionPrefix + "*.txt"))
            {
                var info = ReadSession(file);
                if (info == null) { TryDelete(file); continue; }
                if (IsAlive(info.Value.pid, info.Value.startUtc)) continue;     // этот Revit ещё работает

                var s = info.Value;
                try { File.Copy(file, Path.Combine(PluginStore.DataDir, LastCrashFileName), true); } catch { }
                TryDelete(file);
                var report = Analyze(s);
                if (report != null) Pending = report;                       // показать последнее падение
            }
        }

        static CrashReport Analyze(SessionInfo s)
        {
            var when = $"({s.startedLocal:dd.MM.yyyy HH:mm})";
            // До отметки «loaded» нельзя точно отличить конец загрузки от открытия окна Grasshopper
            var phase = s.loaded ? "вскоре после загрузки Grasshopper" : "при загрузке или открытии Grasshopper";
            var report = new CrashReport { Rhino = s.rhino, ProfilePath = s.profile, WhenUtc = s.startUtcSession, AfterLoad = s.loaded };

            // 1. Последняя запись — ошибка в коде плагина
            if (s.lastKind == "exc" && s.lastExc.HasValue)
            {
                var e = s.lastExc.Value;
                var group = PluginStore.GroupOfFile(e.path, s.rhino);
                PluginStore.Log($"Прошлый запуск Revit завершился аварийно {phase} {when}. Последняя запись перед падением — " +
                                $"ошибка в коде {e.name} ({e.path}): {e.text}" + (group != null ? $" (плагин {group.Value.key})" : " (вне папок плагинов)"));
                if (group != null)
                {
                    report.Kind = CrashKind.PluginError;
                    report.File = e.path; report.Assembly = e.name; report.Error = e.text;
                    report.Key = group.Value.key; report.Name = group.Value.name;
                    AddSuspect(new Suspect { Key = report.Key, Name = report.Name, File = e.path, Assembly = e.name, Rhino = s.rhino, WhenUtc = s.startUtcSession });
                    return report;
                }
            }
            // 2. Упал после загрузки, и последней записью была не ошибка плагина: причиной может быть что угодно
            else if (s.loaded)
            {
                PluginStore.Log($"Прошлый запуск Revit завершился аварийно после загрузки Grasshopper {when}" +
                                (s.watchEnded ? "" : $", в первые {WatchAfterLoad.TotalSeconds:0} с") + "; подозреваемого нет. " +
                                $"Последние загруженные плагины: {string.Join(", ", s.gha.Skip(Math.Max(0, s.gha.Count - 3)).Select(g => Describe(g)))}");
                return null;
            }
            else if (s.gha.Count == 0)
            {
                PluginStore.Log($"Прошлый запуск Revit завершился аварийно в начале загрузки Grasshopper {when}, до загрузки плагинов");
                return null;
            }
            // 3. Упал во время загрузки — последний загружавшийся плагин
            else
            {
                var last = s.gha[s.gha.Count - 1];
                var path = last.path;
                if (string.IsNullOrEmpty(path)) path = PluginStore.FindGhaByAssemblyName(last.name, s.rhino);
                var group = PluginStore.GroupOfFile(path, s.rhino);
                report.File = path; report.Assembly = last.name;
                report.Name = group?.name ?? (string.IsNullOrEmpty(path) ? last.name : Path.GetFileNameWithoutExtension(path));
                PluginStore.Log($"Прошлый запуск Revit завершился аварийно при загрузке или открытии Grasshopper {when}. " +
                                $"Последним загружался: {Describe(last)}" + (group != null ? $" (плагин {group.Value.key})" : " (вне папок плагинов)"));
                if (group != null)
                {
                    report.Kind = CrashKind.LastLoaded;
                    report.Key = group.Value.key;
                    AddSuspect(new Suspect { Key = report.Key, Name = report.Name, File = path, Assembly = last.name, Rhino = s.rhino, WhenUtc = s.startUtcSession });
                    return report;
                }
            }

            // 4. Виновник не определён: загруженные в той сессии плагины с ошибками проверки (✖)
            report.Kind = CrashKind.Unknown;
            report.Key = null;
            try { report.Candidates = IncompatibleLoaded(s); }
            catch (Exception ex) { PluginStore.Log("Маркер падения: проверка плагинов не удалась: " + ex.Message); }
            PluginStore.Log(report.Candidates.Count > 0
                ? "Точный виновник не определён. Загружались несовместимые плагины (✖): " + string.Join(", ", report.Candidates.Select(c => c.key))
                : "Точный виновник не определён; несовместимых плагинов (✖) среди загруженных нет");
            return report;
        }

        /// Плагины, загружавшиеся в упавшей сессии, у которых проверка находит ошибки (✖).
        static List<(string key, string name)> IncompatibleLoaded(SessionInfo s)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> byName = null;
            foreach (var g in s.gha)
            {
                var path = g.path;
                if (string.IsNullOrEmpty(path))
                {
                    byName ??= PluginStore.GhaByAssemblyName(s.rhino);
                    byName.TryGetValue(g.name ?? "", out path);
                }
                var group = PluginStore.GroupOfFile(path, s.rhino);
                if (group != null) keys.Add(group.Value.key);
            }
            var result = new List<(string key, string name)>();
            if (keys.Count == 0) return result;

            var groups = PluginStore.Scan(s.rhino).Where(g => keys.Contains(g.Key)).ToList();
            var issues = PluginChecker.Check(groups, PluginChecker.SnapshotLoaded(), new CheckContext { RhinoMajor = s.rhino });
            foreach (var g in groups)
                if (issues.TryGetValue(g.Key, out var list) && list.Any(i => i.Level == IssueLevel.Error))
                    result.Add((g.Key, g.Name));
            return result;
        }

        /// Главный файл плагина Grasshopper: .gha (C#) или .ghpy (Python), в отличие от его библиотек .dll.
        static bool IsMainPluginFile(string path) =>
            path.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase);

        static string Describe((string path, string name) g) =>
            string.IsNullOrEmpty(g.path) ? g.name + " (из памяти)" : g.path;

        struct SessionInfo
        {
            public int pid; public DateTime startUtc; public int rhino; public string profile;
            public DateTime startUtcSession; public DateTime startedLocal; public bool loaded; public bool watchEnded;
            public List<(string path, string name)> gha;
            public string lastKind;                                                   // gha / exc / watch-end
            public (string path, string name, string text)? lastExc;
        }

        static SessionInfo? ReadSession(string file)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { return null; }
            var s = new SessionInfo { gha = new List<(string, string)>() };
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                switch (p[0])
                {
                    case "pid": int.TryParse(p.ElementAtOrDefault(1), out s.pid); break;
                    case "start": DateTime.TryParse(p.ElementAtOrDefault(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out s.startUtc); break;
                    case "rhino": int.TryParse(p.ElementAtOrDefault(1), out s.rhino); break;
                    case "profile": s.profile = p.ElementAtOrDefault(1); break;
                    case "started":
                        DateTime.TryParse(p.ElementAtOrDefault(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out s.startUtcSession);
                        s.startedLocal = s.startUtcSession.ToLocalTime();
                        break;
                    case "gha":
                        s.gha.Add((p.ElementAtOrDefault(2) ?? "", p.ElementAtOrDefault(3) ?? ""));
                        s.lastKind = "gha";
                        break;
                    case "exc":
                        s.lastExc = (p.ElementAtOrDefault(2) ?? "", p.ElementAtOrDefault(3) ?? "", p.ElementAtOrDefault(4) ?? "");
                        s.lastKind = "exc";
                        break;
                    // «loaded» — только метка времени: сама по себе Revit не роняет и последнюю ошибку плагина не перекрывает
                    case "loaded": s.loaded = true; break;
                    case "watch-end": s.watchEnded = true; s.lastKind = "watch-end"; break;
                }
            }
            if (s.pid <= 0) return null;
            if (s.rhino <= 0) s.rhino = Session.RhinoMajorOrDefault;
            return s;
        }

        static bool IsAlive(int pid, DateTime startUtc)
        {
            if (pid == OwnPid) return false;                // наш номер процесса у старого файла — это прошлый процесс
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    try { return (p.StartTime.ToUniversalTime() - startUtc).Duration() <= StartTolerance; }
                    catch { return true; }                      // нет доступа — не трогаем
                }
            }
            catch (ArgumentException) { return false; }         // процесса нет
            catch { return true; }
        }

        // ---------- Запись сессии ----------

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var asm = args.LoadedAssembly;
                if (asm.IsDynamic) return;
                var name = asm.GetName().Name;
                if (!_active)
                {
                    if (string.Equals(name, "Grasshopper", StringComparison.OrdinalIgnoreCase)) StartSession(asm);
                    return;
                }
                string location = "";
                try { location = asm.Location ?? ""; } catch { }
                // Плагины GH: файлы .gha; при загрузке из памяти (COFF) пути нет — сопоставим по имени при разборе.
                // После загрузки GH сборки без пути не пишем: это в основном код скриптовых компонентов.
                bool gha = IsMainPluginFile(location);
                if (gha || (location.Length == 0 && !_loadedMarked))
                {
                    lock (_lock)
                    {
                        _loaded.Add((location, name));
                        _lastPluginLoadUtc = DateTime.UtcNow;
                        Append($"gha\t{Now()}\t{location}\t{name}");
                        _lastExcSig = null;
                    }
                }
            }
            catch { }
        }

        static void StartSession(Assembly grasshopper)
        {
            lock (_lock)
            {
                if (_active) return;
                _active = true;
                _lastPluginLoadUtc = DateTime.UtcNow;
                try
                {
                    _ghDocumentEditor = grasshopper.GetType("Grasshopper.Instances", false)?
                        .GetProperty("DocumentEditor", BindingFlags.Public | BindingFlags.Static);
                    _loadTimer = new System.Threading.Timer(OnLoadTimer, null, 1000, 1000);
                }
                catch (Exception ex) { PluginStore.Log("Маркер падения: таймер загрузки не запущен: " + ex.Message); }
                try
                {
                    _rhino = Session.RhinoMajorOrDefault;
                    _rootsFull = new List<string>();
                    foreach (var (_, root) in PluginStore.Roots(_rhino))
                        try { _rootsFull.Add(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar); } catch { }
                    string profile = "";
                    try { profile = PluginStore.ActiveProfilePath(); } catch { }
                    Directory.CreateDirectory(PluginStore.DataDir);
                    File.WriteAllLines(SessionPath, new[]
                    {
                        "# RIR_PluginManager: Grasshopper load log of this Revit session. Deleted when Revit closes normally.",
                        "pid\t" + OwnPid.ToString(CultureInfo.InvariantCulture),
                        "start\t" + OwnStartUtc.ToString("o", CultureInfo.InvariantCulture),
                        "revit\t" + PluginStore.RevitVersion,
                        "rhino\t" + _rhino.ToString(CultureInfo.InvariantCulture),
                        "profile\t" + profile,
                        "started\t" + Now()
                    });
                    PluginStore.Log("Grasshopper начал загрузку: ведётся журнал загрузки плагинов (маркер падения)");
                }
                catch (Exception ex) { PluginStore.Log("Маркер падения: не удалось начать запись: " + ex.Message); }
            }
        }

        static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        static void Append(string line)
        {
            // Открыть, дописать, закрыть: после закрытия данные у Windows и переживут падение процесса
            try { File.AppendAllText(SessionPath, line + Environment.NewLine); } catch { }
        }

        /// Grasshopper загрузил плагины. Вызывается по таймеру (окно Grasshopper создано и плагины
        /// больше не загружаются) или из Idling — что наступит раньше. Idling приходит, только когда
        /// пользователь в окне Revit, поэтому без таймера отметка могла запаздывать на минуты.
        public static void MarkLoaded() => MarkLoaded("простой Revit");

        static void MarkLoaded(string source)
        {
            lock (_lock)
            {
                if (!_active || _loadedMarked) return;
                _loadedMarked = true;
                _loadedAtUtc = DateTime.UtcNow;
                Append($"loaded\t{Now()}");
                _lastExcSig = null;
                // Таймер продолжает работать: через WatchAfterLoad он закончит запись ошибок (watch-end),
                // даже если пользователь всё это время в окне Grasshopper и Idling не приходит.
            }
            PluginStore.Log($"Маркер падения: Grasshopper загружен ({source})");
        }

        static void OnLoadTimer(object state)
        {
            try
            {
                if (!_active || _watchEnded) { StopLoadTimer(); return; }
                if (_loadedMarked)
                {
                    if (WatchDue) EndWatch();
                    return;
                }
                if (DateTime.UtcNow - _lastPluginLoadUtc < QuietAfterLoad) return;
                if (_ghDocumentEditor == null || _ghDocumentEditor.GetValue(null) == null) return;   // окно ещё не создано
                MarkLoaded("окно Grasshopper открыто");
            }
            catch { }
        }

        static void StopLoadTimer()
        {
            var t = System.Threading.Interlocked.Exchange(ref _loadTimer, null);
            try { t?.Dispose(); } catch { }
        }

        public static bool NeedsLoadedMark => _active && !_loadedMarked;

        static bool WatchDue => _loadedMarked && DateTime.UtcNow - _loadedAtUtc > WatchAfterLoad;

        /// Вызывается из Idling: закончить запись ошибок плагинов, когда прошло WatchAfterLoad после загрузки.
        public static void Tick()
        {
            if (_active && !_watchEnded && WatchDue) EndWatch();
        }

        static void EndWatch()
        {
            lock (_lock)
            {
                if (_watchEnded || !_active) return;
                _watchEnded = true;
                Append($"watch-end\t{Now()}");
                StopLoadTimer();
            }
            PluginStore.Log($"Маркер падения: наблюдение закончено, записано ошибок в коде плагинов: {_excCount}");
        }

        // ---------- Ошибки в коде плагинов ----------

        /// Вызывается для каждого исключения (FirstChance). Если в стеке вызовов есть код из папок
        /// плагинов, записывает строку exc с файлом сборки ближайшего к ошибке такого кадра.
        /// Ошибки, в стеке которых только код Grasshopper, Rhino или Revit (например, разбор .gha
        /// самим Grasshopper), плагину не приписываются.
        public static void NoteException(Exception ex)
        {
            if (!_active || _watchEnded || _inNote || ex == null) return;
            _inNote = true;
            try
            {
                if (WatchDue) { EndWatch(); return; }
                // Старые ресурсы (иконки) на .NET 9+: ошибку перехватывает и исправляет IconFix — это не сбой плагина.
                // Правило то же, что для лога (App.OnFirstChance).
                if (IconFix.Active && ex is PlatformNotSupportedException &&
                    Environment.StackTrace.Contains("System.Resources.ResourceManager.GetObject"))
                    return;
                var frames = new StackTrace(1, false).GetFrames();
                if (frames == null) return;
                // Ближайший к ошибке кадр из файла плагина (.gha, .ghpy); если его нет — из библиотеки в папке плагина.
                // Сам файл плагина важнее: общую библиотеку (например, MonoMod) мог загрузить из своей папки другой плагин.
                Assembly dllAsm = null; string dllPath = null;
                foreach (var frame in frames)
                {
                    MethodBase method;
                    try { method = frame.GetMethod(); } catch { continue; }
                    if (method == null) continue;
                    Assembly asm;
                    try { asm = method.Module.Assembly; } catch { continue; }
                    if (asm == null || asm == Self) continue;
                    var path = PluginFileOf(asm);
                    if (path == null) continue;
                    if (IsMainPluginFile(path)) { WriteExc(asm, path, ex); return; }
                    if (dllAsm == null) { dllAsm = asm; dllPath = path; }
                }
                if (dllAsm != null) WriteExc(dllAsm, dllPath, ex);
            }
            catch { }
            finally { _inNote = false; }
        }

        /// Файл сборки, если она из папок плагинов (.gha или её библиотека); иначе null.
        static string PluginFileOf(Assembly asm)
        {
            lock (_lock)
            {
                if (_asmPlugin.TryGetValue(asm, out var cached)) return cached.Length == 0 ? null : cached;
            }
            string result = "";
            try
            {
                if (!asm.IsDynamic)
                {
                    string location = "";
                    try { location = asm.Location ?? ""; } catch { }
                    if (location.Length > 0)
                    {
                        var full = Path.GetFullPath(location);
                        if (_rootsFull != null && _rootsFull.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                            result = full;
                    }
                    else
                    {
                        // Загружена из памяти (режим COFF): .gha из папок плагинов с тем же именем сборки
                        var map = _ghaByName ??= PluginStore.GhaByAssemblyName(_rhino);
                        if (map.TryGetValue(asm.GetName().Name ?? "", out var gha)) result = gha;
                    }
                }
            }
            catch { }
            lock (_lock) { _asmPlugin[asm] = result; }
            return result.Length == 0 ? null : result;
        }

        static void WriteExc(Assembly asm, string path, Exception ex)
        {
            string name = "";
            try { name = asm.GetName().Name ?? ""; } catch { }
            var text = ExceptionText(ex);
            var sig = name + "|" + text;
            lock (_lock)
            {
                if (!_active || _watchEnded) return;
                if (sig == _lastExcSig || _excCount >= MaxExcLines) return;
                _excCount++;
                _lastExcSig = sig;
                Append($"exc\t{Now()}\t{path}\t{name}\t{text}");
            }
        }

        /// «Тип: сообщение → исходная причина: сообщение», в одну строку.
        static string ExceptionText(Exception ex)
        {
            string One(Exception x)
            {
                string msg;
                try { msg = x.Message; } catch { msg = ""; }
                return x.GetType().Name + ": " + msg;
            }
            var text = One(ex);
            var root = ex;
            while (root.InnerException != null) root = root.InnerException;
            if (root != ex) text += " → " + One(root);
            text = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            return text.Length > 500 ? text.Substring(0, 500) + "…" : text;
        }

        /// Штатное закрытие Revit: снять подозрения с плагинов, которые в этой сессии загрузились
        /// без падения, и удалить файл сессии.
        public static void EndSession()
        {
            lock (_lock)
            {
                if (!_active) return;
                try
                {
                    var suspects = _loadedMarked && _loaded.Count > 0 ? ReadSuspects() : new List<Suspect>();
                    if (suspects.Count > 0)
                    {
                        int before = suspects.Count;
                        // Плагин загрузился в этой сессии: по группе его файла (подозреваемым может быть
                        // и библиотека плагина) или, для загруженных из памяти, по имени сборки.
                        var keys = new HashSet<string>(_loaded.Where(l => !string.IsNullOrEmpty(l.path))
                                                              .Select(l => PluginStore.GroupOfFile(l.path, _rhino)?.key)
                                                              .Where(k => k != null), StringComparer.OrdinalIgnoreCase);
                        suspects.RemoveAll(s => s.Rhino == _rhino && (keys.Contains(s.Key) || _loaded.Any(l =>
                            (!string.IsNullOrEmpty(l.path) && string.Equals(l.path, s.File, StringComparison.OrdinalIgnoreCase)) ||
                            (string.IsNullOrEmpty(l.path) && string.Equals(l.name, s.Assembly, StringComparison.OrdinalIgnoreCase)))));
                        if (suspects.Count != before)
                        {
                            WriteSuspects(suspects);
                            PluginStore.Log($"Сняты подозрения в падении: {before - suspects.Count} (плагины загрузились без сбоя)");
                        }
                    }
                }
                catch (Exception ex) { PluginStore.Log("Маркер падения: " + ex.Message); }
                TryDelete(SessionPath);
                _active = false;
                StopLoadTimer();
            }
        }

        // ---------- Подозреваемые ----------

        public static List<Suspect> ReadSuspects()
        {
            var list = new List<Suspect>();
            try
            {
                if (!File.Exists(SuspectsPath)) return list;
                foreach (var line in File.ReadAllLines(SuspectsPath))
                {
                    var s = Suspect.Parse(line);
                    if (s != null) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        static void WriteSuspects(List<Suspect> list)
        {
            if (list.Count == 0) { TryDelete(SuspectsPath); return; }
            var lines = new List<string>
            {
                "# RIR_PluginManager: plugins suspected of crashing Revit while Grasshopper was loading or opening.",
                "# key<TAB>name<TAB>file<TAB>assembly<TAB>Rhino<TAB>crash (UTC). A line is removed after a normal session with the plugin."
            };
            lines.AddRange(list.Select(s => s.Format()));
            File.WriteAllLines(SuspectsPath, lines);
        }

        static void AddSuspect(Suspect s)
        {
            var list = ReadSuspects();
            list.RemoveAll(x => x.Rhino == s.Rhino && string.Equals(x.Key, s.Key, StringComparison.OrdinalIgnoreCase));
            list.Add(s);
            WriteSuspects(list);
        }

        /// Подозреваемые для текущей версии Rhino, по ключу группы.
        public static Dictionary<string, Suspect> SuspectsForCurrentRhino()
        {
            int rhino = Session.RhinoMajorOrDefault;
            var map = new Dictionary<string, Suspect>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ReadSuspects().Where(s => s.Rhino == rhino)) map[s.Key] = s;
            return map;
        }

        /// Включённые (не отключённые в профиле) плагины, на которых уже падал Revit.
        public static List<Suspect> EnabledSuspects(ISet<string> disabledKeys) =>
            SuspectsForCurrentRhino().Values.Where(s => !disabledKeys.Contains(s.Key)).ToList();

        /// Текст для предупреждения: «WiresRenderer (падение 09.10.2026)».
        public static string List(IEnumerable<Suspect> suspects) =>
            string.Join(", ", suspects.Select(s => $"{s.Name} (падение {s.WhenUtc.ToLocalTime():dd.MM.yyyy})"));

        /// Сообщение о прошлом падении показано (или обработано).
        public static void ClearPending() => Pending = null;

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
