using System;
using System.Collections.Generic;
using System.IO;

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// Нужен компилятору для свойств с init в сборке под .NET Framework 4.8.
    internal static class IsExternalInit { }
}
#endif

namespace RIR_PluginManager
{
    /// Общие помощники, одинаково работающие на .NET Framework 4.8 и .NET 10.
    internal static class FileUtil
    {
        /// Рекурсивный обход папки: недоступные подпапки пропускаются, а не прерывают весь обход.
        public static List<string> EnumerateFiles(string root, string pattern)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try { result.AddRange(Directory.GetFiles(dir, pattern)); } catch { }
                try { foreach (var sub in Directory.GetDirectories(dir)) stack.Push(sub); } catch { }
            }
            return result;
        }

        /// Путь file относительно папки root (аналог Path.GetRelativePath для вложенных путей).
        public static string RelativePath(string root, string file)
        {
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var f = Path.GetFullPath(file);
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f.Substring(r.Length) : Path.GetFileName(f);
        }
    }
}
