using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace BiruBox.Loader;

/// <summary>
/// Permanent, immutable bootstrapper for BiruBox.
/// Registered in BiruBox.addin. Because its binary hash never changes across updates,
/// Autodesk Revit retains the "Always Load" trust without prompting the user on future updates.
/// </summary>
public class AppLoader : IExternalApplication
{
    private IExternalApplication? _realApp;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            string currentDir = Path.GetDirectoryName(typeof(AppLoader).Assembly.Location) ?? string.Empty;
            string targetDll = Path.Combine(currentDir, "BiruBox.dll");

            if (!File.Exists(targetDll))
            {
                TaskDialog.Show("BiruBox Loader", $"BiruBox core assembly not found at:\n{targetDll}");
                return Result.Failed;
            }

            // Hook assembly resolver for dependencies located in the same add-in directory
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    string? simpleName = new AssemblyName(args.Name).Name;
                    if (string.IsNullOrEmpty(simpleName)) return null;

                    string candidate = Path.Combine(currentDir, simpleName + ".dll");
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }
                catch
                {
                    // Ignore and let standard resolution proceed
                }
                return null;
            };

            // Dynamically load the real BiruBox assembly
            Assembly realAssembly = Assembly.LoadFrom(targetDll);
            Type? appType = realAssembly.GetType("BiruBox.App");
            if (appType == null)
            {
                TaskDialog.Show("BiruBox Loader", "Entry point 'BiruBox.App' was not found in BiruBox.dll.");
                return Result.Failed;
            }

            _realApp = (IExternalApplication?)Activator.CreateInstance(appType);
            if (_realApp == null)
            {
                TaskDialog.Show("BiruBox Loader", "Failed to create instance of 'BiruBox.App'.");
                return Result.Failed;
            }

            return _realApp.OnStartup(application);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("BiruBox Loader Error", ex.ToString());
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
