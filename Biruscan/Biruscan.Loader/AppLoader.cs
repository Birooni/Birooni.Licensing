using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace Biruscan.Loader;

/// <summary>
/// Permanent bootstrapper registered in Biruscan.addin. The file hash must stay
/// unchanged across auto-updates so Revit keeps Always Load after install.
/// </summary>
public class AppLoader : IExternalApplication
{
    private IExternalApplication? _realApp;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            string currentDir = Path.GetDirectoryName(typeof(AppLoader).Assembly.Location) ?? string.Empty;
            string targetDll = Path.Combine(currentDir, "Biruscan.dll");

            if (!File.Exists(targetDll))
            {
                TaskDialog.Show("Biruscan", $"Biruscan core assembly not found at:\n{targetDll}");
                return Result.Failed;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    string? simpleName = new AssemblyName(args.Name).Name;
                    if (string.IsNullOrEmpty(simpleName)) return null;
                    string candidate = Path.Combine(currentDir, simpleName + ".dll");
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                }
                catch
                {
                    return null;
                }
            };

            Assembly realAssembly = Assembly.LoadFrom(targetDll);
            Type? appType = realAssembly.GetType("Biruscan.App");
            if (appType == null)
            {
                TaskDialog.Show("Biruscan", "Entry point 'Biruscan.App' was not found in Biruscan.dll.");
                return Result.Failed;
            }

            _realApp = (IExternalApplication?)Activator.CreateInstance(appType);
            if (_realApp == null)
            {
                TaskDialog.Show("Biruscan", "Failed to create instance of 'Biruscan.App'.");
                return Result.Failed;
            }

            return _realApp.OnStartup(application);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Biruscan Error", ex.ToString());
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            return _realApp?.OnShutdown(application) ?? Result.Succeeded;
        }
        catch
        {
            return Result.Succeeded;
        }
    }
}
