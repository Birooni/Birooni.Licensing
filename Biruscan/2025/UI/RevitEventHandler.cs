using System;
using Autodesk.Revit.UI;

namespace Biruscan.UI
{
    public class RevitEventHandler : IExternalEventHandler
    {
        private Action _action;

        public void SetAction(Action action)
        {
            _action = action;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                _action?.Invoke();
                if (app.ActiveUIDocument != null)
                {
                    app.ActiveUIDocument.RefreshActiveView();
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", "Error in external event: " + ex.Message);
            }
        }

        public string GetName()
        {
            return "Biruscan External Event";
        }
    }
}
