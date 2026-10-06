using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RIR_PluginManager
{
    [Transaction(TransactionMode.Manual)]
    public class ShowManagerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;

            var window = new ManagerWindow();
            new System.Windows.Interop.WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            if (window.ShowDialog() != true) return Result.Cancelled;

            string commandName = window.Next switch
            {
                NextAction.Rhino => App.CmdRhino,
                NextAction.Grasshopper => App.CmdGrasshopper,
                _ => null
            };
            if (commandName == null) return Result.Succeeded;

            // Команда RiR запустится сразу после завершения этой команды
            try
            {
                var id = RevitCommandId.LookupCommandId(commandName);
                if (id == null)
                {
                    TaskDialog.Show("RIR_PluginManager",
                        "Не найдена команда Rhino.Inside:\n" + commandName +
                        "\n\nПлагины уже применены, нажмите кнопку Rhino или Grasshopper вручную.");
                    return Result.Succeeded;
                }
                // Команды RiR недоступны, пока Rhino.Inside не запущен (журнал Revit:
                // "cannot be invoked in this context"). Тогда сначала запускаем RiR.
                if (!uiapp.CanPostCommand(id))
                {
                    var start = RevitCommandId.LookupCommandId(App.CmdStart);
                    if (start != null && uiapp.CanPostCommand(start))
                    {
                        uiapp.PostCommand(start);
                        TaskDialog.Show("RIR_PluginManager",
                            "Плагины применены. Rhino.Inside ещё не был запущен, поэтому сейчас он запустится.\n\n" +
                            "После загрузки нажмите кнопку Rhino или Grasshopper.");
                    }
                    else
                    {
                        TaskDialog.Show("RIR_PluginManager",
                            "Плагины применены, но команда Rhino.Inside сейчас недоступна.\n\n" +
                            "Нажмите Start на вкладке Rhino.Inside, затем Rhino или Grasshopper.");
                    }
                    return Result.Succeeded;
                }
                uiapp.PostCommand(id);
            }
            catch (Exception ex)
            {
                PluginStore.Log("PostCommand error: " + ex.Message);
                TaskDialog.Show("RIR_PluginManager", "Не удалось запустить команду Rhino.Inside: " + ex.Message);
            }
            return Result.Succeeded;
        }
    }
}
