# RIR_PluginManager

<img width="1714" height="1354" alt="RIR_PM" src="https://github.com/user-attachments/assets/df7df86c-f196-4ce1-9e51-399852d9edc6" />

https://github.com/user-attachments/assets/80185950-af57-4c70-aec8-1c79a293435d

## English

If Revit crashes when you start Rhino or Grasshopper through Rhino.Inside.Revit, this might help.

I'd like to share an add-in for Rhino.Inside.Revit that lets you turn off loading of installed Grasshopper plugins.

Rhino.Inside.Revit runs inside the Revit process, which ties Rhino and Grasshopper to the .NET version of Revit itself.

| Group | Revit | Runtime | Rhino in Rhino.Inside |
| --- | --- | --- | --- |
| Group A | 2021 – 2024 | .NET Framework 4.8 | 7, 8 |
| Group B | 2025.0 – 2025.4, 2026.0 – 2026.4 | .NET 8 | 8 |
| Group C | 2025.5+, 2026.5+, 2027+ | .NET 10 | 8 (8.32+), 9 |

| Rhino | Runtime |
| --- | --- |
| Rhino 7 | .NET Framework 4.8 |
| Rhino 8.0 – 8.19 | .NET 7 |
| Rhino 8.20 - 8.35 | .NET 8 |
| Rhino 8.36+ | .NET 10 |
| Rhino 9 | .NET 10 |

*.NET Framework 4.8 is optional for Rhino 8-9

This leads to the following problems with plugins in each group:

**Group A:**
Revit 2021–2024 runs on .NET Framework 4.8, so Rhino 8 inside Revit also runs on .NET Framework, not on .NET 7/8 like standalone Rhino.
- Plugins built only for .NET 7/8 don't load.
- If such a plugin runs code when Grasshopper opens, Revit crashes.
- Plugins for Rhino 8 don't load in Rhino 7.

**Group B:**
Revit 2025.0–2025.4 and 2026.0–2026.4 run on .NET 8, the same as standalone Rhino 8. This group has the fewest problems.
- Plugins built for a newer .NET don't load.
- Shared library conflicts can occur (see below).

**Group C:**
Revit 2025.5+, 2026.5+ and 2027 run on .NET 10.
- Older plugins store their icons in the `BinaryFormatter` format, which was removed in .NET 10. Because of this, Revit can crash when Grasshopper opens.
- Old Python components (`.ghpy`, IronPython 2.7) don't load.

**In all groups:**
- If several plugins use different versions of the same library, only one version is loaded, and some plugins stop working.
- When Revit crashes, it doesn't tell you which plugin caused it.

**What the add-in does:**
- Checks plugins without loading them and shows which ones won't work in your Revit version, and why.
- Lets you turn off loading of selected plugins in Grasshopper inside Revit. Selections are saved as profiles. All plugins stay available in standalone Rhino.
- Restores old icons without `BinaryFormatter`, so Revit doesn't crash because of them.
- Preloads the newest version of shared libraries, so plugins don't conflict.
- If Revit does crash, at the next start it names the plugin that caused it and offers to disable it.

> **Important:** the add-in does not fix plugins. Its main purpose is to warn you which Grasshopper plugins may crash Revit and to let you exclude them from loading. A plugin that is incompatible with your Revit version stays incompatible until its author releases a suitable version.

Supports Revit 2021–2027, Rhino 7, 8 and 9.

### Installation

1. Close Revit.
2. On the [Releases](../../releases) page download the archive for your Revit version: `RIR_PluginManager_v<version>_R_<Revit version>.zip` — `R_2021` … `R_2024`, `R_2027`; Revit 2025 and 2026 have two each: `R_2025.0-2025.4` / `R_2025.5+` and `R_2026.0-2026.4` / `R_2026.5+` (the update number is shown in Help → About). Builds for different Revit versions are not interchangeable.
3. Extract it into `%APPDATA%\Autodesk\Revit\Addins\<Revit version>\` (you can paste this path into the Explorer address bar). The result should look like this:

   ```
   %APPDATA%\Autodesk\Revit\Addins\2027\
   ├── RIR_PluginManager.addin
   └── RIR_PluginManager\
       ├── RIR_PluginManager.dll
       └── … other DLLs from the archive
   ```

4. Start Revit. When asked about an unsigned add-in, choose **Always Load**.

If the add-in does not appear, open the properties of the DLL files and click **Unblock** (Windows marks files downloaded from the internet).

The buttons **Plugin Manager** and **Grasshopper (profile)** appear on the Rhino.Inside tab. The interface follows the Revit language (Russian Revit → Russian, any other → English); you can switch it in the Plugin Manager window (**Язык / Language**). Detailed documentation below is in Russian.


---

## Русский

Если вы сталкиваетесь с вылетами Revit при подключении к Rhino или Grasshopper через Rhino.Inside.Revit, то это решение для вас.

Хочу поделиться надстройкой для Rhino.Inside.Revit, которая позволяет отключать загрузку установленных плагинов в Grasshopper.

Rhino.Inside.Revit запускается внутри процесса Revit, и это жестко привязывает Rhino и Grasshopper к версии .NET самого Revit.

| Group | Revit | Runtime | Rhino в Rhino.Inside |
| --- | --- | --- | --- |
| Group A | 2021 – 2024 | .NET Framework 4.8 | 7, 8 |
| Group B | 2025.0 – 2025.4, 2026.0 – 2026.4 | .NET 8 | 8 |
| Group C | 2025.5+, 2026.5+, 2027+ | .NET 10 | 8 (8.32+), 9 |

| Rhino | Runtime |
| --- | --- |
| Rhino 7 | .NET Framework 4.8 |
| Rhino 8.0 – 8.19 | .NET 7 |
| Rhino 8.20 - 8.35 | .NET 8 |
| Rhino 8.36+ | .NET 10 |
| Rhino 9 | .NET 10 |

*.NET Framework 4.8 для Rhino 8-9 опционально

Это приводит к следующим выявленным проблемам в группах при использовании плагинов:

**Group A:**
Revit 2021–2024 работает на .NET Framework 4.8, поэтому Rhino 8 внутри Revit тоже работает на .NET Framework, а не на .NET 7/8, как отдельный Rhino.
- Плагины, собранные только под .NET 7/8, не загружаются.
- Если такой плагин выполняет код при открытии Grasshopper, Revit вылетает.
- Плагины для Rhino 8 не загружаются в Rhino 7.

**Group B:**
Revit 2025.0–2025.4 и 2026.0–2026.4 работают на .NET 8, как и отдельный Rhino 8. Проблем здесь меньше всего.
- Плагины, собранные под более новый .NET, не загружаются.
- Бывают конфликты общих библиотек (см. ниже).

**Group C:**
Revit 2025.5+, 2026.5+ и 2027 работают на .NET 10.
- Старые плагины хранят иконки в формате `BinaryFormatter`, который в .NET 10 удалён. Из-за этого Revit может вылететь при открытии Grasshopper.
- Старые Python-компоненты (`.ghpy`, IronPython 2.7) не загружаются.

**Во всех группах:**
- Если несколько плагинов используют одну библиотеку разных версий, загружается только одна, и часть плагинов перестаёт работать.
- При вылете Revit не сообщает, какой плагин виноват.

**Что делает надстройка:**
- Проверяет плагины без загрузки и показывает, какие из них не будут работать в вашей версии Revit и почему.
- Позволяет отключить загрузку выбранных плагинов в Grasshopper внутри Revit. Наборы сохраняются в профили. В обычном Rhino все плагины остаются доступными.
- Восстанавливает старые иконки без `BinaryFormatter`, чтобы Revit не вылетал из-за них.
- Заранее загружает самую новую версию общих библиотек, чтобы плагины не конфликтовали.
- Если Revit всё-таки вылетел, при следующем запуске называет плагин, из-за которого это произошло, и предлагает его отключить.

> **Важно:** надстройка не чинит плагины. Её главная задача — предупредить, какие плагины Grasshopper могут привести к вылету Revit, и позволить исключить их из загрузки. Несовместимый с вашей версией Revit плагин так и останется несовместимым, пока его автор не выпустит подходящую версию.

Поддерживаются Revit 2021–2027, Rhino 7, 8 и 9.

## Установка из готового архива

1. Закройте Revit.
2. На странице [Releases](../../releases) скачайте архив для своей версии Revit: `RIR_PluginManager_v<версия>_R_<версия Revit>.zip` — `R_2021` … `R_2024`, `R_2027`; для Revit 2025 и 2026 их по два: `R_2025.0-2025.4` / `R_2025.5+` и `R_2026.0-2026.4` / `R_2026.5+` (номер обновления видно в «Справка → О программе»). Архив должен совпадать с годом вашего Revit: сборки для разных версий Revit не взаимозаменяемы.
3. Распакуйте его в папку `%APPDATA%\Autodesk\Revit\Addins\<версия Revit>\` (путь можно вставить в адресную строку Проводника). Должно получиться так:

   ```
   %APPDATA%\Autodesk\Revit\Addins\2027\
   ├── RIR_PluginManager.addin
   └── RIR_PluginManager\
       ├── RIR_PluginManager.dll
       └── … остальные DLL из архива
   ```

   Набор DLL зависит от рантайма: `0Harmony.dll` (.NET 8/10), `System.Formats.Nrbf.dll` (.NET 8), `System.Reflection.Metadata.dll` и `System.Collections.Immutable.dll` (.NET Framework). Копируйте всё содержимое архива.

4. Запустите Revit. На вопрос о неподписанной надстройке ответьте **«Всегда загружать»**.

Если надстройка не появилась, откройте свойства файлов DLL и нажмите **«Разблокировать»** (Windows помечает файлы, скачанные из интернета).
