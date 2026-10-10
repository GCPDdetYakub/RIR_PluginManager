using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RIR_PluginManager
{
    // Повторяет рабочий путь «Применить → Grasshopper» без окна:
    // применяет сохранённый профиль и открывает GH через PostCommand.
    [Transaction(TransactionMode.Manual)]
    public class QuickGrasshopperCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            try
            {
                if (!PluginStore.GrasshopperPluginsLoaded())
                {
                    var profile = PluginStore.LoadProfile();

                    // Включены плагины, на которых Revit уже падал при загрузке или открытии Grasshopper (маркер падения)
                    var present = new System.Collections.Generic.HashSet<string>(
                        System.Linq.Enumerable.Select(PluginStore.Scan(), g => g.Key), StringComparer.OrdinalIgnoreCase);
                    var suspects = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(
                        CrashMarker.EnabledSuspects(profile.Disabled), s => present.Contains(s.Key)));
                    if (suspects.Count > 0)
                    {
                        var td = new TaskDialog("RIR_PluginManager")
                        {
                            MainInstruction = L.T("На этих плагинах Revit уже падал при загрузке или открытии Grasshopper",
                                                  "Revit has already crashed on these plugins while Grasshopper was loading or opening"),
                            MainContent = CrashMarker.List(suspects),
                            CommonButtons = TaskDialogCommonButtons.Cancel
                        };
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, L.T("Отключить их и запустить Grasshopper", "Disable them and start Grasshopper"),
                            L.T($"Они будут отключены в профиле «{profile.Name}».", $"They will be disabled in profile \"{profile.Name}\"."));
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, L.T("Запустить как есть", "Start as is"));
                        var answer = td.Show();
                        if (answer == TaskDialogResult.CommandLink1)
                        {
                            foreach (var s in suspects) profile.Disabled.Add(s.Key);
                            PluginStore.SaveProfile(profile);
                            PluginStore.Log(L.T("Quick: отключены плагины, на которых падал Revit: ", "Quick: disabled plugins on which Revit crashed: ") + CrashMarker.List(suspects));
                        }
                        else if (answer != TaskDialogResult.CommandLink2) return Result.Cancelled;
                    }

                    var errors = PluginStore.Apply(profile, out int moved);
                    PluginStore.Log(L.T($"Quick: профиль «{profile.Name}» применён, отключено файлов: {moved}", $"Quick: profile \"{profile.Name}\" applied, files disabled: {moved}"));
                    SharedLibPreloader.Run(L.T("кнопка Grasshopper (профиль)", "Grasshopper (profile) button"));
                    foreach (var e in errors) PluginStore.Log("Quick: " + e);
                }

                var gh = RevitCommandId.LookupCommandId(App.CmdGrasshopper);
                if (gh == null)
                {
                    TaskDialog.Show("RIR_PluginManager", L.T("Не найдена команда Grasshopper в Rhino.Inside.", "The Grasshopper command of Rhino.Inside was not found."));
                    return Result.Failed;
                }

                if (!uiapp.CanPostCommand(gh))
                {
                    var start = RevitCommandId.LookupCommandId(App.CmdStart);
                    if (start != null && uiapp.CanPostCommand(start))
                    {
                        uiapp.PostCommand(start);
                        TaskDialog.Show("RIR_PluginManager",
                            L.T("Rhino.Inside запускается. После загрузки нажмите «Grasshopper (профиль)» ещё раз.",
                                "Rhino.Inside is starting. When it has loaded, click \"Grasshopper (profile)\" again."));
                    }
                    else
                    {
                        TaskDialog.Show("RIR_PluginManager",
                            L.T("Команда Grasshopper сейчас недоступна. Нажмите Start на вкладке Rhino.Inside и повторите.",
                                "The Grasshopper command is not available now. Click Start on the Rhino.Inside tab and try again."));
                    }
                    return Result.Succeeded;
                }

                uiapp.PostCommand(gh);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginStore.Log("Quick error: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
