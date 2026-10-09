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

        const string PanelName = "Плагины GH";

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
                    inner += Environment.NewLine + "  причина: " + x.GetType().FullName + ": " + x.Message;
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
                PluginStore.Log("==== Запуск Revit: RIR_PluginManager " +
                                Assembly.GetExecutingAssembly().GetName().Version +
                                ", Revit " + application.ControlledApplication.VersionNumber +
                                " (" + application.ControlledApplication.VersionBuild + "), " +
                                Session.RuntimeText + " ====");
                PluginStore.MigrateLegacy();
                PluginStore.EnsureProfileFiles();
                var errors = PluginStore.RecoverOnStartup();
                foreach (var e in errors) PluginStore.Log("Startup: " + e);
                var profile = PluginStore.LoadProfile();

                if (Session.IsNetFramework)
                    PluginStore.Log("IconFix не требуется: .NET Framework");
                else if (profile.IconFix)
                    IconFix.Install();
                else
                    PluginStore.Log("IconFix выключен в настройках");

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
                ToolTip = "Выбрать, какие плагины Grasshopper загружать в Rhino.Inside. " +
                          "Сделайте это до первого открытия Grasshopper в сессии.",
                LargeImage = IconFactory.Make(32),
                Image = IconFactory.Make(16)
            };
            panel.AddItem(data);

            var quick = new PushButtonData(
                "RIR_PluginManager_QuickGH",
                "Grasshopper\n(профиль)",
                Assembly.GetExecutingAssembly().Location,
                typeof(QuickGrasshopperCommand).FullName)
            {
                ToolTip = "Применить сохранённый профиль плагинов и открыть Grasshopper. " +
                          "Если Rhino.Inside ещё не запущен, сначала запускает его.",
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
                PluginStore.Log($"Grasshopper загружен, файлам возвращены имена (ошибок: {errors.Count})");
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
                var when = r.WhenUtc == DateTime.MinValue ? "" : $" ({r.WhenUtc.ToLocalTime():dd.MM.yyyy HH:mm})";
                // «Во время загрузки» — только когда виновник определён по последнему загружавшемуся плагину;
                // ошибка плагина или падение без виновника могли случиться и при открытии окна Grasshopper.
                var phase = r.AfterLoad ? "при открытии Grasshopper"
                          : r.Kind == CrashMarker.CrashKind.LastLoaded ? "во время загрузки Grasshopper"
                          : "при загрузке или открытии Grasshopper";
                var td = new TaskDialog("RIR_PluginManager")
                {
                    MainInstruction = $"Прошлый запуск Revit завершился аварийно {phase}{when}"
                };

                var keys = new System.Collections.Generic.List<string>();
                string disableLabel = null;
                string keepDescription = null;
                switch (r.Kind)
                {
                    case CrashMarker.CrashKind.PluginError:
                        td.MainContent = $"Перед падением произошла ошибка в коде плагина «{r.Name}». Вероятно, Revit упал из-за него." +
                                         "\n\n" + r.Error + (string.IsNullOrEmpty(r.File) ? "" : "\n\n" + r.File);
                        keys.Add(r.Key);
                        disableLabel = $"Отключить «{r.Name}»";
                        keepDescription = "Перед запуском Grasshopper надстройка напомнит об этом плагине.";
                        break;

                    case CrashMarker.CrashKind.LastLoaded:
                        td.MainContent = $"Последним загружался плагин «{r.Name}». Вероятно, Revit упал из-за него." +
                                         (string.IsNullOrEmpty(r.File) ? "" : "\n\n" + r.File);
                        keys.Add(r.Key);
                        disableLabel = $"Отключить «{r.Name}»";
                        keepDescription = "Перед запуском Grasshopper надстройка напомнит об этом плагине.";
                        break;

                    default:
                        td.MainContent = "Определить плагин, из-за которого упал Revit, не удалось." +
                                         (string.IsNullOrEmpty(r.File) ? "" : $"\n\nПоследним загружался: {r.File}");
                        if (r.Candidates.Count > 0)
                        {
                            td.MainContent += "\n\nВ той сессии загружались плагины, несовместимые с этой версией Revit " +
                                              "(✖ в проверке плагинов): " + string.Join(", ", r.Candidates.ConvertAll(c => c.name)) +
                                              ". Вероятнее всего, причина в одном из них.";
                            keys.AddRange(r.Candidates.ConvertAll(c => c.key));
                            disableLabel = r.Candidates.Count == 1 ? $"Отключить «{r.Candidates[0].name}»" : $"Отключить их ({r.Candidates.Count})";
                        }
                        else
                            td.MainContent += "\n\nНесовместимых плагинов среди загружавшихся не найдено. " +
                                              "Запись загрузки сохранена в файле last-crash.txt в папке надстройки.";
                        break;
                }

                bool profileExists = !string.IsNullOrEmpty(r.ProfilePath) && System.IO.File.Exists(r.ProfilePath);
                bool canDisable = keys.Count > 0 && profileExists;
                if (canDisable)
                {
                    var profileName = System.IO.Path.GetFileNameWithoutExtension(r.ProfilePath);
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, disableLabel,
                        $"В профиле «{profileName}» (Rhino {r.Rhino}). Подействует при следующем запуске Grasshopper.");
                    if (keepDescription != null)
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Оставить включённым", keepDescription);
                    else
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Оставить как есть");
                }
                else
                {
                    if (keys.Count > 0)
                        td.MainContent += "\n\nПрофиль той сессии не найден: отключить плагин можно в окне Plugin Manager.";
                    td.CommonButtons = TaskDialogCommonButtons.Close;
                }
                if (td.Show() == TaskDialogResult.CommandLink1 && canDisable)
                    foreach (var key in keys) PluginStore.AddDisabledToProfileFile(r.ProfilePath, key);
            }
            catch (Exception ex) { PluginStore.Log("Сообщение о падении: " + ex.Message); }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            CrashMarker.EndSession();
            if (IconFix.Active) PluginStore.Log(IconFix.Summary() + " (за сессию)");
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
