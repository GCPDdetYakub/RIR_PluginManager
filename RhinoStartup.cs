using System;
using System.Linq;
using System.Reflection;

namespace RIR_PluginManager
{
    /// Действия в момент запуска Rhino внутри Revit (загрузка RhinoCommon), то есть до того,
    /// как Grasshopper начнёт загружать плагины. В этот момент точно известна версия Rhino,
    /// поэтому здесь применяется профиль этой версии и выполняется предзагрузка общих библиотек.
    public static class RhinoStartup
    {
        static bool _done;
        static readonly object _lock = new object();

        public static void Arm()
        {
            var rc = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => { try { return a.GetName().Name == "RhinoCommon"; } catch { return false; } });
            if (rc != null)
            {
                Run(rc.GetName().Version, L.T("Rhino уже загружен", "Rhino already loaded"));
                return;
            }
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            PluginStore.Log(L.T("Ожидание запуска Rhino (применение профиля и предзагрузка библиотек)", "Waiting for Rhino to start (profile and library preload)"));
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var name = args.LoadedAssembly.GetName();
                if (!string.Equals(name.Name, "RhinoCommon", StringComparison.OrdinalIgnoreCase)) return;
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                Run(name.Version, L.T("запуск Rhino", "Rhino start"));
            }
            catch (Exception ex)
            {
                PluginStore.Log(L.T("Запуск Rhino: ошибка ", "Rhino start: error ") + ex.Message);
            }
        }

        static void Run(Version rhinoCommon, string reason)
        {
            lock (_lock)
            {
                if (_done) return;
                _done = true;
            }

            try
            {
                Session.SetRhinoFromRhinoCommon(rhinoCommon);
                PluginStore.Log($"{reason}: {Session.Describe()}");

                if (PluginStore.GrasshopperPluginsLoaded()) return;

                var profile = PluginStore.LoadProfile();
                var suspects = CrashMarker.EnabledSuspects(profile.Disabled);
                if (suspects.Count > 0)
                    PluginStore.Log(L.T("Внимание: включены плагины, на которых Revit уже падал при загрузке или открытии Grasshopper: ",
                                        "Warning: enabled plugins on which Revit already crashed while loading or opening Grasshopper: ") + CrashMarker.List(suspects));
                if (profile.AutoApply)
                {
                    var errors = PluginStore.Apply(profile, out int moved);
                    PluginStore.Log(L.T($"Профиль «{profile.Name}» применён автоматически при запуске Rhino, отключено файлов: {moved}",
                                        $"Profile \"{profile.Name}\" applied automatically at Rhino start, files disabled: {moved}"));
                    foreach (var e in errors) PluginStore.Log("AutoApply: " + e);
                }

                SharedLibPreloader.Run(reason);
            }
            catch (Exception ex)
            {
                PluginStore.Log(L.T("Запуск Rhino: ошибка ", "Rhino start: error ") + ex);
            }
        }
    }
}
