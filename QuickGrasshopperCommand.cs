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
                    var errors = PluginStore.Apply(profile, out int moved);
                    PluginStore.Log($"Quick: профиль «{profile.Name}» применён, отключено файлов: {moved}");
                    SharedLibPreloader.Run("кнопка Grasshopper (профиль)");
                    foreach (var e in errors) PluginStore.Log("Quick: " + e);
                }

                var gh = RevitCommandId.LookupCommandId(App.CmdGrasshopper);
                if (gh == null)
                {
                    TaskDialog.Show("RIR_PluginManager", "Не найдена команда Grasshopper в Rhino.Inside.");
                    return Result.Failed;
                }

                if (!uiapp.CanPostCommand(gh))
                {
                    var start = RevitCommandId.LookupCommandId(App.CmdStart);
                    if (start != null && uiapp.CanPostCommand(start))
                    {
                        uiapp.PostCommand(start);
                        TaskDialog.Show("RIR_PluginManager",
                            "Rhino.Inside запускается. После загрузки нажмите «Grasshopper (профиль)» ещё раз.");
                    }
                    else
                    {
                        TaskDialog.Show("RIR_PluginManager",
                            "Команда Grasshopper сейчас недоступна. Нажмите Start на вкладке Rhino.Inside и повторите.");
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
