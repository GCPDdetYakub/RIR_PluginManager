using System;
using System.IO;

namespace RIR_PluginManager
{
    /// Язык интерфейса и журнала (русский / английский).
    ///
    /// Выбор хранится один раз для всех версий Revit в общей папке
    /// %APPDATA%\Autodesk\Revit\Addins\RIR_PluginManager\settings.txt (строка language=auto|ru|en).
    /// auto — по языку Revit: русский Revit → русский интерфейс, любой другой → английский.
    ///
    /// Тексты записаны в коде парами: L.T("русский", "English"). Файлы ресурсов .NET не используются,
    /// потому что чтение ресурсов перехватывает IconFix.
    public static class L
    {
        public const string Auto = "auto", Ru = "ru", En = "en";
        const string FileName = "settings.txt";

        static string _revitLanguage = "";

        /// Выбор пользователя: auto, ru или en.
        public static string Setting { get; private set; } = Auto;

        /// Интерфейс на английском.
        public static bool English { get; private set; }

        static string FilePath => Path.Combine(DisabledJournal.Dir, FileName);

        /// Вызывается при запуске Revit. revitLanguage — значение ControlledApplication.Language ("Russian", "English_USA" …).
        public static void Init(string revitLanguage)
        {
            _revitLanguage = revitLanguage ?? "";
            Setting = ReadSetting();
            Apply();
        }

        public static string T(string ru, string en) => English ? en : ru;

        /// Сохранить выбор языка (действует сразу; надписи на ленте Revit — после перезапуска).
        public static void Save(string setting)
        {
            setting = Normalize(setting);
            Directory.CreateDirectory(DisabledJournal.Dir);
            File.WriteAllLines(FilePath, new[]
            {
                "# RIR_PluginManager: settings shared by all Revit versions",
                "# language=auto - Revit language (Russian Revit -> Russian, any other -> English); ru; en",
                "language=" + setting
            });
            Setting = setting;
            Apply();
        }

        static void Apply()
        {
            English = Setting == En ||
                      (Setting == Auto && _revitLanguage.IndexOf("Russian", StringComparison.OrdinalIgnoreCase) < 0);
        }

        static string ReadSetting()
        {
            try
            {
                if (!File.Exists(FilePath)) return Auto;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("#")) continue;
                    var i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i).Trim().Equals("language", StringComparison.OrdinalIgnoreCase))
                        return Normalize(line.Substring(i + 1));
                }
            }
            catch { }
            return Auto;
        }

        static string Normalize(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return v == Ru || v == En ? v : Auto;
        }
    }
}
