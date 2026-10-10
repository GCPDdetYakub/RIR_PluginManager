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

            // После смены языка окно закрывается и открывается заново с теми же отметками
            ManagerWindow window;
            ManagerWindow.Carry carry = null;
            while (true)
            {
                window = new ManagerWindow(carry);
                new System.Windows.Interop.WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
                var ok = window.ShowDialog();
                if (window.Reopen != null) { carry = window.Reopen; continue; }
                if (ok != true) return Result.Cancelled;
                break;
            }

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
                        L.T("Не найдена команда Rhino.Inside:\n", "Rhino.Inside command not found:\n") + commandName +
                        L.T("\n\nПлагины уже применены, нажмите кнопку Rhino или Grasshopper вручную.",
                            "\n\nPlugins have been applied; click the Rhino or Grasshopper button manually."));
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
                            L.T("Плагины применены. Rhino.Inside ещё не был запущен, поэтому сейчас он запустится.\n\nПосле загрузки нажмите кнопку Rhino или Grasshopper.",
                                "Plugins applied. Rhino.Inside was not running yet, so it will start now.\n\nWhen it has loaded, click the Rhino or Grasshopper button."));
                    }
                    else
                    {
                        TaskDialog.Show("RIR_PluginManager",
                            L.T("Плагины применены, но команда Rhino.Inside сейчас недоступна.\n\nНажмите Start на вкладке Rhino.Inside, затем Rhino или Grasshopper.",
                                "Plugins applied, but the Rhino.Inside command is not available now.\n\nClick Start on the Rhino.Inside tab, then Rhino or Grasshopper."));
                    }
                    return Result.Succeeded;
                }
                uiapp.PostCommand(id);
            }
            catch (Exception ex)
            {
                PluginStore.Log("PostCommand error: " + ex.Message);
                TaskDialog.Show("RIR_PluginManager", L.T("Не удалось запустить команду Rhino.Inside: ", "Could not start the Rhino.Inside command: ") + ex.Message);
            }
            return Result.Succeeded;
        }
    }
}
