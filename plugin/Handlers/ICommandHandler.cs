using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace RevitMcpPlugin.Handlers
{
    public interface ICommandHandler
    {
        string Execute(Dictionary<string, string> args, Document doc, UIApplication uiApp);
    }
}
