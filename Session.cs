using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RIR_PluginManager
{
    /// Сведения о текущем сеансе: рантайм процесса Revit и версия Rhino, с которой работает Rhino.Inside.
    public static class Session
    {
        /// Revit 2021–2024 работают на .NET Framework 4.8; Revit 2025.5+ — на .NET 10.
        public static bool IsNetFramework { get; } =
            RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);

        /// Основной номер версии рантайма: 4 для .NET Framework, 8/10/... для .NET.
        public static int RuntimeMajor { get; } = IsNetFramework ? 4 : Environment.Version.Major;

        public static string RuntimeText => RuntimeInformation.FrameworkDescription.Trim();

        static int _rhino;
        static string _rhinoSource;

        /// Основной номер версии Rhino (7, 8, 9...). 0 — пока не удалось определить.
        public static int RhinoMajor { get { Detect(); return _rhino; } }

        /// Версия Rhino для путей и имени профиля: если определить не удалось, считается Rhino 8.
        public static int RhinoMajorOrDefault => RhinoMajor > 0 ? RhinoMajor : 8;

        /// Откуда взята версия Rhino (для лога и заголовка окна).
        public static string RhinoSource { get { Detect(); return _rhinoSource ?? "не определена, принята 8"; } }

        /// Вызывается при загрузке RhinoCommon: это точное значение.
        public static void SetRhinoFromRhinoCommon(Version v)
        {
            if (v == null || v.Major <= 0) return;
            _rhino = v.Major;
            _rhinoSource = "RhinoCommon " + v;
        }

        /// Порядок: загруженный RhinoCommon (точно) → папка R7/R8/R9 сборки Rhino.Inside
        /// (Rhino.Inside выбирает её в окне выбора Rhino при запуске Revit).
        static void Detect()
        {
            if (_rhinoSource != null && _rhinoSource.StartsWith("RhinoCommon")) return;   // точное значение уже есть

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = a.GetName();
                    if (string.Equals(name.Name, "RhinoCommon", StringComparison.OrdinalIgnoreCase))
                    {
                        SetRhinoFromRhinoCommon(name.Version);
                        return;
                    }
                }
                catch { }
            }

            if (_rhino > 0) return;                                    // уже определено по папке Rhino.Inside

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.IsDynamic) continue;
                    var n = a.GetName().Name;
                    if (n == null || !n.StartsWith("RhinoInside.Revit", StringComparison.OrdinalIgnoreCase)) continue;
                    int major = MajorFromPath(a.Location);
                    if (major > 0)
                    {
                        _rhino = major;
                        _rhinoSource = "папка Rhino.Inside R" + major;
                        return;
                    }
                }
                catch { }
            }
        }

        /// Ищет в пути папку вида R7, R8, R9.
        static int MajorFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                var seg = Path.GetFileName(dir);
                if (seg != null && seg.Length >= 2 && (seg[0] == 'R' || seg[0] == 'r') &&
                    int.TryParse(seg.Substring(1), out int n) && n >= 5 && n <= 20)
                    return n;
                dir = Path.GetDirectoryName(dir);
            }
            return 0;
        }

        /// Строка вида "Revit 2027 · Rhino 8 · .NET 10.0.12".
        public static string Describe() =>
            $"Revit {PluginStore.RevitVersion} · Rhino {(RhinoMajor > 0 ? RhinoMajor.ToString() : "?")} · {RuntimeText}";
    }
}
