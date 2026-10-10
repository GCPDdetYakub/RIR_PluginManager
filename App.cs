using System;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RIR_PluginManager
{
    public class App : IExternalApplication
    {
        // Имена вкладки и команд Rhino.Inside взяты из журнала Revit 2027 (RiR 1.35)
        public const string RirTab = "Rhino.Inside";
        public const string CmdRhino = "CustomCtrl_%CustomCtrl_%Rhino.Inside%Rhinoceros%CommandRhino";
        public const string CmdGrasshopper = "CustomCtrl_%CustomCtrl_%Rhino.Inside%Grasshopper%CommandGrasshopper";
        public const string CmdStart = "CustomCtrl_%CustomCtrl_%Rhino.Inside%More%CommandStart";

        // Имя панели на ленте (на языке интерфейса; смена языка действует после перезапуска Revit)
        static string PanelName => L.T("Плагины GH", "GH Plugins");

        // Отключение нужно только на время, пока Grasshopper сканирует папки.
        // Как только он загрузился, файлы можно вернуть: GH в Revit их уже не перечитает,
        // а отдельный Rhino снова будет видеть все плагины.
        static readonly Stopwatch _throttle = Stopwatch.StartNew();
        static DateTime? _ghSeenAt;

        // Диагностика: записать в лог полный стек при PlatformNotSupportedException
        // (BinaryFormatter в .NET 10), чтобы понять, чей код его вызывает.
        [ThreadStatic] static bool _inFirstChance;
        static int _firstChanceCount;

        // Исключения, которые помогают понять причины проблем с плагинами на .NET 10.
        static bool IsInteresting(Exception ex)
        {
            switch (ex)
            {
                case PlatformNotSupportedException _:                 // BinaryFormatter и др. удалённые API
                case System.IO.FileLoadException _:                   // конфликт версий сборок
                case BadImageFormatException _:
                case TypeLoadException _:
                case MissingMethodException _:
                case MissingFieldException _:
                case TypeInitializationException _:                   // ошибка статического конструктора (например, Harmony/MonoMod в плагине)
                    return true;
                case System.IO.FileNotFoundException cecil when cecil.GetType().FullName.StartsWith("Mono.Cecil"):
                    return false;                                     // предварительный анализ сборок Grasshopper, безвреден
                case System.IO.FileNotFoundException fnf:             // только отсутствующие сборки, не любые файлы
                    var m = fnf.Message ?? "";
                    return m.IndexOf("assembly", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           m.IndexOf(".resources", StringComparison.OrdinalIgnoreCase) < 0;
                default:
                    return false;
            }
        }

        static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (_inFirstChance) return;
            _inFirstChance = true;
            try
            {
                // Маркер падения: ошибка в коде плагина (любого типа) во время загрузки и открытия Grasshopper
                CrashMarker.NoteException(e.Exception);

                if (!IsInteresting(e.Exception)) return;
                var stack = Environment.StackTrace;
                // Ошибки чтения иконок перехватывает IconFix: в лог они не пишутся, итог см. строку "IconFix: восстановлено ..."
                if (IconFix.Active && e.Exception is PlatformNotSupportedException &&
                    stack.Contains("System.Resources.ResourceManager.GetObject"))
                    return;

                int n = System.Threading.Interlocked.Increment(ref _firstChanceCount);
                // Полный стек вызова в момент исключения
                // У TypeInitializationException и подобных настоящая причина — во вложенных исключениях
                var inner = "";
                for (var x = e.Exception.InnerException; x != null; x = x.InnerException)
                    inner += Environment.NewLine + L.T("  причина: ", "  cause: ") + x.GetType().FullName + ": " + x.Message;
                PluginStore.Log($"FirstChance #{n} {e.Exception.GetType().FullName}: {e.Exception.Message}" + inner +
                                Environment.NewLine + stack);
            }
            catch { }
            finally { _inFirstChance = false; }
        }

        static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try { PluginStore.Log("Unhandled exception: " + e.ExceptionObject); } catch { }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            // 1. Если прошлая сессия упала, вернуть файлам исходные имена.
            // 2. Восстановление иконок (только .NET 9+) ставится сразу, до запуска Rhino.
            // 3. Профиль (он свой для каждой версии Rhino) применяется при запуске Rhino.
            try
            {
                PluginStore.RevitVersion = application.ControlledApplication.VersionNumber;
                // Язык интерфейса и журнала: выбор пользователя или язык Revit (до первой записи в журнал)
                string revitLanguage = "";
                try { revitLanguage = application.ControlledApplication.Language.ToString(); } catch { }
                L.Init(revitLanguage);
                PluginStore.Log(L.T("==== Запуск Revit: RIR_PluginManager ", "==== Revit start: RIR_PluginManager ") +
                                Assembly.GetExecutingAssembly().GetName().Version +
                                ", Revit " + application.ControlledApplication.VersionNumber +
                                " (" + application.ControlledApplication.VersionBuild + "), " +
                                Session.RuntimeText + " ====");
                PluginStore.Log(L.T($"Язык: {(L.English ? "английский" : "русский")} (настройка: {L.Setting}, язык Revit: {revitLanguage})",
                                    $"Language: {(L.English ? "English" : "Russian")} (setting: {L.Setting}, Revit language: {revitLanguage})"));
                PluginStore.MigrateLegacy();
                PluginStore.EnsureProfileFiles();
                var errors = PluginStore.RecoverOnStartup();
                foreach (var e in errors) PluginStore.Log("Startup: " + e);
                var profile = PluginStore.LoadProfile();

                if (Session.IsNetFramework)
                    PluginStore.Log(L.T("IconFix не требуется: .NET Framework", "IconFix not needed: .NET Framework"));
                else if (profile.IconFix)
                    IconFix.Install();
                else
                    PluginStore.Log(L.T("IconFix выключен в настройках", "IconFix is turned off in settings"));

                // Маркер падения: разобрать прошлый запуск и следить за загрузкой Grasshopper
                CrashMarker.Arm();
                RhinoStartup.Arm();
            }
            catch (Exception ex)
            {
                PluginStore.Log("Startup error: " + ex);
            }

            application.Idling += OnIdling;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

            // Кнопка: на вкладке Rhino.Inside, если она уже создана, иначе на вкладке «Надстройки».
            RibbonPanel panel = null;
            try { panel = application.CreateRibbonPanel(RirTab, PanelName); }
            catch { panel = null; }
            if (panel == null)
            {
                try { panel = application.CreateRibbonPanel(PanelName); }
                catch (Exception ex)
                {
                    PluginStore.Log("Ribbon error: " + ex.Message);
                    return Result.Succeeded;
                }
            }

            var data = new PushButtonData(
                "RIR_PluginManager_Show",
                "Plugin\nManager",
                Assembly.GetExecutingAssembly().Location,
                typeof(ShowManagerCommand).FullName)
            {
                ToolTip = L.T("Выбрать, какие плагины Grasshopper загружать в Rhino.Inside. Сделайте это до первого открытия Grasshopper в сессии.",
                              "Choose which Grasshopper plugins load in Rhino.Inside. Do this before Grasshopper is opened for the first time in the session."),
                LargeImage = IconFactory.Make(32),
                Image = IconFactory.Make(16)
            };
            panel.AddItem(data);

            var quick = new PushButtonData(
                "RIR_PluginManager_QuickGH",
                L.T("Grasshopper\n(профиль)", "Grasshopper\n(profile)"),
                Assembly.GetExecutingAssembly().Location,
                typeof(QuickGrasshopperCommand).FullName)
            {
                ToolTip = L.T("Применить сохранённый профиль плагинов и открыть Grasshopper. Если Rhino.Inside ещё не запущен, сначала запускает его.",
                              "Apply the saved plugin profile and open Grasshopper. Starts Rhino.Inside first if it is not running yet."),
                LargeImage = IconFactory.MakePlay(32),
                Image = IconFactory.MakePlay(16)
            };
            panel.AddItem(quick);

            return Result.Succeeded;
        }

        static void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                if (_throttle.ElapsedMilliseconds < 2000) return;
                _throttle.Restart();

                // Прошлый запуск упал при загрузке или открытии Grasshopper — сообщить, когда Revit готов к работе
                if (CrashMarker.Pending != null) ShowCrashReport();
                CrashMarker.Tick();

                if (!PluginStore.HasDisabledFiles && !CrashMarker.NeedsLoadedMark) { _ghSeenAt = null; return; }
                if (!PluginStore.GrasshopperPluginsLoaded()) return;

                // Grasshopper загружает плагины синхронно в UI-потоке, поэтому к первому Idling
                // после загрузки сканирование уже закончено. Небольшая пауза для надёжности.
                if (_ghSeenAt == null) { _ghSeenAt = DateTime.Now; return; }
                if ((DateTime.Now - _ghSeenAt.Value).TotalSeconds < 3) return;

                _ghSeenAt = null;
                CrashMarker.MarkLoaded();
                if (!PluginStore.HasDisabledFiles) return;
                var errors = PluginStore.RestoreOwn();
                PluginStore.Log(L.T($"Grasshopper загружен, файлам возвращены имена (ошибок: {errors.Count})", $"Grasshopper loaded, file names restored (errors: {errors.Count})"));
                if (IconFix.Active) PluginStore.Log(IconFix.Summary());
                foreach (var err in errors) PluginStore.Log("AutoRestore: " + err);
            }
            catch (Exception ex)
            {
                PluginStore.Log("Idling error: " + ex.Message);
            }
        }

        /// Сообщение о падении прошлого запуска с предложением отключить подозреваемый плагин
        /// (или, если виновник не определён, загружавшиеся несовместимые плагины).
        static void ShowCrashReport()
        {
            var r = CrashMarker.Pending;
            CrashMarker.ClearPending();
            try
            {
                var when = r.WhenUtc == DateTime.MinValue ? "" : " (" + r.WhenUtc.ToLocalTime().ToString(L.T("dd.MM.yyyy HH:mm", "yyyy-MM-dd HH:mm")) + ")";
                // «Во время загрузки» — только когда виновник определён по последнему загружавшемуся плагину;
                // ошибка плагина или падение без виновника могли случиться и при открытии окна Grasshopper.
                var phase = r.AfterLoad ? L.T("при открытии Grasshopper", "while Grasshopper was opening")
                          : r.Kind == CrashMarker.CrashKind.LastLoaded ? L.T("во время загрузки Grasshopper", "while Grasshopper was loading")
                          : L.T("при загрузке или открытии Grasshopper", "while Grasshopper was loading or opening");
                var td = new TaskDialog("RIR_PluginManager")
                {
                    MainInstruction = L.T($"Прошлый запуск Revit завершился аварийно {phase}{when}", $"The previous Revit session crashed {phase}{when}")
                };

                var keys = new System.Collections.Generic.List<string>();
                string disableLabel = null;
                string keepDescription = null;
                switch (r.Kind)
                {
                    case CrashMarker.CrashKind.PluginError:
                        td.MainContent = L.T($"Перед падением произошла ошибка в коде плагина «{r.Name}». Вероятно, Revit упал из-за него.",
                                              $"Before the crash an error occurred in the code of plugin \"{r.Name}\". Revit most likely crashed because of it.") +
                                         "\n\n" + r.Error + (string.IsNullOrEmpty(r.File) ? "" : "\n\n" + r.File);
                        keys.Add(r.Key);
                        disableLabel = L.T($"Отключить «{r.Name}»", $"Disable \"{r.Name}\"");
                        keepDescription = L.T("Перед запуском Grasshopper надстройка напомнит об этом плагине.", "The add-in will remind you about this plugin before Grasshopper starts.");
                        break;

                    case CrashMarker.CrashKind.LastLoaded:
                        td.MainContent = L.T($"Последним загружался плагин «{r.Name}». Вероятно, Revit упал из-за него.",
                                              $"The last plugin loaded was \"{r.Name}\". Revit most likely crashed because of it.") +
                                         (string.IsNullOrEmpty(r.File) ? "" : "\n\n" + r.File);
                        keys.Add(r.Key);
                        disableLabel = L.T($"Отключить «{r.Name}»", $"Disable \"{r.Name}\"");
                        keepDescription = L.T("Перед запуском Grasshopper надстройка напомнит об этом плагине.", "The add-in will remind you about this plugin before Grasshopper starts.");
                        break;

                    default:
                        td.MainContent = L.T("Определить плагин, из-за которого упал Revit, не удалось.", "The plugin that crashed Revit could not be determined.") +
                                         (string.IsNullOrEmpty(r.File) ? "" : L.T($"\n\nПоследним загружался: {r.File}", $"\n\nLast loaded: {r.File}"));
                        if (r.Candidates.Count > 0)
                        {
                            var names = string.Join(", ", r.Candidates.ConvertAll(c => c.name));
                            td.MainContent += L.T($"\n\nВ той сессии загружались плагины, несовместимые с этой версией Revit (✖ в проверке плагинов): {names}. Вероятнее всего, причина в одном из них.",
                                                  $"\n\nPlugins incompatible with this Revit version (✖ in the plugin check) were loaded in that session: {names}. Most likely one of them is the cause.");
                            keys.AddRange(r.Candidates.ConvertAll(c => c.key));
                            disableLabel = r.Candidates.Count == 1 ? L.T($"Отключить «{r.Candidates[0].name}»", $"Disable \"{r.Candidates[0].name}\"")
                                                                    : L.T($"Отключить их ({r.Candidates.Count})", $"Disable them ({r.Candidates.Count})");
                        }
                        else
                            td.MainContent += L.T("\n\nНесовместимых плагинов среди загружавшихся не найдено. Запись загрузки сохранена в файле last-crash.txt в папке надстройки.",
                                                  "\n\nNo incompatible plugins were among those loaded. The loading record is saved in last-crash.txt in the add-in folder.");
                        break;
                }

                bool profileExists = !string.IsNullOrEmpty(r.ProfilePath) && System.IO.File.Exists(r.ProfilePath);
                bool canDisable = keys.Count > 0 && profileExists;
                if (canDisable)
                {
                    var profileName = System.IO.Path.GetFileNameWithoutExtension(r.ProfilePath);
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, disableLabel,
                        L.T($"В профиле «{profileName}» (Rhino {r.Rhino}). Подействует при следующем запуске Grasshopper.",
                            $"In profile \"{profileName}\" (Rhino {r.Rhino}). Takes effect the next time Grasshopper starts."));
                    if (keepDescription != null)
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, L.T("Оставить включённым", "Keep enabled"), keepDescription);
                    else
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, L.T("Оставить как есть", "Leave as is"));
                }
                else
                {
                    if (keys.Count > 0)
                        td.MainContent += L.T("\n\nПрофиль той сессии не найден: отключить плагин можно в окне Plugin Manager.", "\n\nThe profile of that session was not found: you can disable the plugin in the Plugin Manager window.");
                    td.CommonButtons = TaskDialogCommonButtons.Close;
                }
                if (td.Show() == TaskDialogResult.CommandLink1 && canDisable)
                    foreach (var key in keys) PluginStore.AddDisabledToProfileFile(r.ProfilePath, key);
            }
            catch (Exception ex) { PluginStore.Log(L.T("Сообщение о падении: ", "Crash message: ") + ex.Message); }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            CrashMarker.EndSession();
            if (IconFix.Active) PluginStore.Log(IconFix.Summary() + L.T(" (за сессию)", " (per session)"));
            try
            {
                foreach (var e in PluginStore.RestoreOwn()) PluginStore.Log("Shutdown: " + e);
            }
            catch (Exception ex)
            {
                PluginStore.Log("Shutdown error: " + ex);
            }
            return Result.Succeeded;
        }
    }
}
