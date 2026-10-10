#if !NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Formats.Nrbf;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Threading;
using HarmonyLib;

namespace RIR_PluginManager
{
    /// Восстановление ресурсов (иконок), сохранённых старыми плагинами в формате BinaryFormatter.
    ///
    /// В .NET 9+ BinaryFormatter удалён, и ResourceManager.GetObject для таких ресурсов
    /// выбрасывает PlatformNotSupportedException. Здесь этот вызов перехватывается (Harmony, finalizer):
    /// если он упал, данные ресурса читаются как структура через System.Formats.Nrbf
    /// (официальный безопасный разборщик формата, код плагина не выполняется),
    /// из них достаются байты картинки, и вызывающему возвращается готовый Bitmap или Icon.
    /// Все остальные ресурсы и ошибки проходят как обычно.
    public static class IconFix
    {
        public static bool Active { get; private set; }
        public static string Status { get; private set; } = L.T("не запускался", "not started");

        static int _restored, _failed;
        static readonly ConcurrentDictionary<string, Recovered> _cache = new ConcurrentDictionary<string, Recovered>();
        static readonly FieldInfo MainAssemblyField =
            typeof(ResourceManager).GetField("MainAssembly", BindingFlags.Instance | BindingFlags.NonPublic);

        sealed class Recovered
        {
            public byte[] Data;
            public bool IsIcon;

            public object Create()
            {
                // Поток не закрываем: GDI+ требует, чтобы он жил вместе с Bitmap
                var ms = new MemoryStream(Data, writable: false);
                if (IsIcon) return new Icon(ms);
                return new Bitmap(ms);
            }
        }

        /// Типы ресурсов, которые умеем восстанавливать.
        public static bool IsSupportedType(string resourceTypeName)
        {
            if (string.IsNullOrEmpty(resourceTypeName)) return false;
            var t = resourceTypeName.Split(',')[0].Trim();
            return t == "System.Drawing.Bitmap" || t == "System.Drawing.Icon";
        }

        public static void Install()
        {
            try
            {
                if (MainAssemblyField == null)
                    throw new MissingFieldException("ResourceManager.MainAssembly");

                var harmony = new Harmony("rir_pluginmanager.iconfix");
                var finalizer = new HarmonyMethod(typeof(IconFix).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic));

                // В .NET оба публичных GetObject сходятся во внутреннем GetObject(string, CultureInfo, bool)
                var inner = AccessTools.Method(typeof(ResourceManager), "GetObject",
                    new[] { typeof(string), typeof(CultureInfo), typeof(bool) });
                if (inner != null)
                {
                    harmony.Patch(inner, finalizer: finalizer);
                }
                else
                {
                    harmony.Patch(AccessTools.Method(typeof(ResourceManager), "GetObject", new[] { typeof(string) }), finalizer: finalizer);
                    harmony.Patch(AccessTools.Method(typeof(ResourceManager), "GetObject", new[] { typeof(string), typeof(CultureInfo) }), finalizer: finalizer);
                }

                Active = true;
                Status = L.T("работает", "active");
                PluginStore.Log(L.T("IconFix: перехват ResourceManager.GetObject установлен", "IconFix: ResourceManager.GetObject hook installed") +
                                (inner != null ? L.T(" (внутренний метод)", " (internal method)") : L.T(" (публичные методы)", " (public methods)")));
            }
            catch (Exception ex)
            {
                Active = false;
                Status = L.T("не установлен: ", "not installed: ") + ex.Message;
                PluginStore.Log(L.T("IconFix не установлен: ", "IconFix not installed: ") + ex);
            }
        }

        public static string Summary() =>
            L.T($"IconFix: восстановлено ресурсов {_restored}, не удалось {_failed}", $"IconFix: resources restored {_restored}, failed {_failed}");

        // Имена параметров Harmony: __exception, __instance, __args, __result
        static Exception Finalizer(Exception __exception, ResourceManager __instance, object[] __args, ref object __result)
        {
            if (__exception == null) return null;
            if (!(__exception is NotSupportedException)) return __exception;     // PlatformNotSupportedException наследует его

            try
            {
                var name = __args != null && __args.Length > 0 ? __args[0] as string : null;
                if (name == null || __instance == null) return __exception;

                var obj = TryRecover(__instance, name);
                if (obj == null)
                {
                    Interlocked.Increment(ref _failed);
                    return __exception;
                }

                __result = obj;
                Interlocked.Increment(ref _restored);
                return null;                                   // исключение поглощено, вызывающий получил картинку
            }
            catch
            {
                return __exception;
            }
        }

        static object TryRecover(ResourceManager rm, string name)
        {
            var asm = MainAssemblyField.GetValue(rm) as Assembly;
            var baseName = rm.BaseName;
            if (asm == null || string.IsNullOrEmpty(baseName)) return null;

            var key = asm.FullName + "|" + baseName + "|" + name;
            var entry = _cache.GetOrAdd(key, _ => Decode(asm, baseName, name));
            return entry?.Create();
        }

        static Recovered Decode(Assembly asm, string baseName, string name)
        {
            try
            {
                using (var stream = asm.GetManifestResourceStream(baseName + ".resources"))
                {
                    if (stream == null) return null;
                    using (var reader = new ResourceReader(stream))
                    {
                        reader.GetResourceData(name, out string type, out byte[] data);
                        if (data == null || !IsSupportedType(type)) return null;

                        var root = NrbfDecoder.Decode(new MemoryStream(data));
                        if (!(root is ClassRecord cr)) return null;

                        var typeName = cr.TypeName.FullName;
                        if (typeName == "System.Drawing.Bitmap")
                        {
                            var bytes = GetBytes(cr, "Data");
                            return bytes == null ? null : new Recovered { Data = bytes };
                        }
                        if (typeName == "System.Drawing.Icon")
                        {
                            var bytes = GetBytes(cr, "IconData");
                            return bytes == null ? null : new Recovered { Data = bytes, IsIcon = true };
                        }
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginStore.Log(L.T($"IconFix: не удалось разобрать ресурс {name} в {asm.GetName().Name}: {ex.Message}",
                                    $"IconFix: could not parse resource {name} in {asm.GetName().Name}: {ex.Message}"));
                return null;
            }
        }

        static byte[] GetBytes(ClassRecord cr, string member)
        {
            if (!cr.HasMember(member)) return null;
            return (cr.GetArrayRecord(member) as SZArrayRecord<byte>)?.GetArray();
        }
    }
}
#else
namespace RIR_PluginManager
{
    /// На .NET Framework (Revit 2021–2024) BinaryFormatter есть, восстанавливать иконки не нужно.
    public static class IconFix
    {
        public static bool Active => false;
        public static string Status => L.T("не требуется на .NET Framework", "not needed on .NET Framework");
        public static bool IsSupportedType(string resourceTypeName) => false;
        public static void Install() { }
        public static string Summary() => "";
    }
}
#endif
