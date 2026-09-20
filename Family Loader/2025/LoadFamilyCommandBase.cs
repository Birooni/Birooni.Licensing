using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FamilyLoader
{
    [System.Reflection.Obfuscation(Exclude = true)]
    [Transaction(TransactionMode.Manual)]
    public abstract class LoadFamilyCommandBase : IExternalCommand
    {
        private readonly int _commandIndex;

        protected LoadFamilyCommandBase(int commandIndex)
        {
            _commandIndex = commandIndex;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!FamilyLoader.Core.LicensingManager.EnsureLicense())
            {
                return Result.Cancelled;
            }

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "No active Revit document found.";
                return Result.Failed;
            }

            Document doc = uidoc.Document;
            // Look up mapping from App
            var mapping = App.GetCommandMapping(_commandIndex);
            if (mapping == null || string.IsNullOrEmpty(mapping.RfaPath))
            {
                TaskDialog.Show("Family Loader", "This command is not mapped to a valid family.");
                return Result.Failed;
            }

            string rfaPath = mapping.RfaPath;
            string targetTypeName = mapping.TypeName;
            
            if (mapping.IsMainButton)
            {
                if (App.LastSelectedType.TryGetValue(rfaPath, out string? lastType))
                {
                    targetTypeName = lastType;
                }
                else
                {
                    targetTypeName = mapping.TypeName; // Will fallback to loading first type found
                }
            }
            else if (mapping.IsLoadAllButton)
            {
                targetTypeName = "";
            }

            string familyName = System.IO.Path.GetFileNameWithoutExtension(rfaPath);

            if (!System.IO.File.Exists(rfaPath))
            {
                TaskDialog.Show("Family Loader", $"Family file could not be found at path:\n\n{rfaPath}\n\nPlease check your settings.");
                return Result.Failed;
            }

            try
            {
                FamilySymbol? symbolToPlace = null;
                bool isAlreadyLoaded = false;
                Family? existingFamily = null;

                // Check if the family is already loaded in the document
                FilteredElementCollector collector = new FilteredElementCollector(doc);
                existingFamily = collector.OfClass(typeof(Family))
                                          .Cast<Family>()
                                          .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (existingFamily != null)
                {
                    isAlreadyLoaded = true;
                }

                using (Transaction tx = new Transaction(doc, $"Load & Activate Type {targetTypeName}"))
                {
                    tx.Start();

                    // If a specific type name is known, try to load it specifically
                    if (!string.IsNullOrEmpty(targetTypeName))
                    {
                        if (isAlreadyLoaded && existingFamily != null)
                        {
                            symbolToPlace = FindSymbolByName(doc, existingFamily, targetTypeName);
                        }

                        if (symbolToPlace == null)
                        {
                            TryLoadFamilySymbol(doc, rfaPath, targetTypeName, out symbolToPlace);
                        }

                        if (symbolToPlace == null)
                        {
                            Family? loadedFamily = TryLoadFamily(doc, rfaPath);
                            if (loadedFamily != null)
                            {
                                existingFamily = loadedFamily;
                                symbolToPlace = FindSymbolByName(doc, loadedFamily, targetTypeName);
                            }
                        }
                    }
                    else
                    {
                        // Fallback if type name is unknown (e.g. only 1 type, or parsing failed)
                        if (!isAlreadyLoaded || mapping.IsLoadAllButton)
                        {
                            Family? loadedFamily = TryLoadFamily(doc, rfaPath);
                            if (loadedFamily != null) existingFamily = loadedFamily;
                        }
                        
                        if (existingFamily != null)
                        {
                            if (mapping.IsLoadAllButton)
                            {
                                foreach (ElementId id in existingFamily.GetFamilySymbolIds())
                                {
                                    var sym = doc.GetElement(id) as FamilySymbol;
                                    if (sym != null && !sym.IsActive) sym.Activate();
                                }
                            }

                            symbolToPlace = FindFirstSymbol(doc, existingFamily);
                        }
                    }

                    if (symbolToPlace != null && !symbolToPlace.IsActive)
                    {
                        symbolToPlace.Activate();
                        doc.Regenerate();
                    }

                    if (symbolToPlace == null)
                        tx.RollBack();
                    else
                        tx.Commit();
                }

                if (symbolToPlace == null)
                {
                    TaskDialog.Show("Family Loader", $"Failed to retrieve the family type symbol '{targetTypeName}'.");
                    return Result.Failed;
                }

                if (!mapping.IsMainButton && !string.IsNullOrEmpty(targetTypeName))
                {
                    App.LastSelectedType[rfaPath] = targetTypeName;
                }

                // Launch Revit interactive family placement tool (must run OUTSIDE a transaction)
                try
                {
                    uidoc.PromptForFamilyInstancePlacement(symbolToPlace);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    TaskDialog.Show("Family Loader", $"Cannot place the family '{symbolToPlace.FamilyName}' in the current view.\n\nPlease ensure you are in a valid view (e.g., Floor Plan) for this type of family, and that no other command is currently active.");
                    return Result.Failed;
                }
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User pressed ESC to cancel the placement, this is normal
                // Must return Succeeded, otherwise Revit undoes the placed instances!
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Family Loader Error", $"An error occurred while loading/placing the family:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        private static void TryLoadFamilySymbol(Document doc, string rfaPath, string typeName, out FamilySymbol? symbol)
        {
            symbol = null;
            try
            {
                doc.LoadFamilySymbol(rfaPath, typeName, out symbol);
            }
            catch
            {
                symbol = null;
            }
        }

        private static Family? TryLoadFamily(Document doc, string rfaPath)
        {
            try
            {
                doc.LoadFamily(rfaPath, new DefaultFamilyLoadOptions(), out Family loadedFamily);
                return loadedFamily;
            }
            catch
            {
                return null;
            }
        }

        private static FamilySymbol? FindSymbolByName(Document doc, Family family, string typeName)
        {
            foreach (ElementId id in family.GetFamilySymbolIds())
            {
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                if (doc.GetElement(id) is FamilySymbol symbol &&
                    symbol.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return symbol;
                }
            }

            return null;
        }

        private static FamilySymbol? FindFirstSymbol(Document doc, Family family)
        {
            foreach (ElementId id in family.GetFamilySymbolIds())
            {
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                if (doc.GetElement(id) is FamilySymbol symbol)
                    return symbol;
            }

            return null;
        }
    }

    public class DefaultFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
