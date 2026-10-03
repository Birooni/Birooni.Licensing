using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Biruscan.Models;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace Biruscan.Services
{
    public static class ClippingSyncService
    {
        private static readonly Guid SchemaGuid = new Guid("9A7B3E2D-5F1C-4E8B-B2D1-A9C3F8E4D7B6");

        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
            {
                SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
                builder.SetSchemaName("BiruscanClippingState");
                builder.AddSimpleField("ClippingStateJson", typeof(string));
                schema = builder.Finish();
            }
            return schema;
        }

        public static void SaveState(Document doc, PointCloudInstance pci, ObservableCollection<ClippingGroup> groups)
        {
            if (pci == null) return;
            
            try
            {
                // Serialize the entire state of groups and cuts
                string json = JsonSerializer.Serialize(groups);

                Entity entity = new Entity(GetSchema());
                entity.Set("ClippingStateJson", json);
                
                pci.SetEntity(entity);
            }
            catch { }
        }

        /// <summary>Erases the persisted clipping state stored on the instance.</summary>
        public static void ClearState(Document doc, PointCloudInstance pci)
        {
            if (pci == null) return;
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema != null)
                {
                    Entity entity = pci.GetEntity(schema);
                    if (entity != null && entity.IsValid())
                        pci.DeleteEntity(schema);
                }
            }
            catch { }
        }

        public static void SyncUiFromState(PointCloudInstance pci, ObservableCollection<ClippingGroup> groups)
        {
            if (pci == null) return;

            try
            {
                Schema schema = GetSchema();
                Entity entity = pci.GetEntity(schema);
                if (!entity.IsValid()) 
                {
                    // No entity means Revit undid everything. 
                    // Just uncheck all items in the UI, but DO NOT delete them!
                    UI.Views.ClippingManagerWindow.IsSyncingFromRevit = true;
                    foreach (var g in groups)
                    {
                        foreach (var c in g.Clippings) c.IsActive = false;
                    }
                    UI.Views.ClippingManagerWindow.IsSyncingFromRevit = false;
                    return;
                }

                string json = entity.Get<string>("ClippingStateJson");
                if (string.IsNullOrEmpty(json)) return;

                List<ClippingGroup> deserializedGroups = JsonSerializer.Deserialize<List<ClippingGroup>>(json);

                if (deserializedGroups != null)
                {
                    UI.Views.ClippingManagerWindow.IsSyncingFromRevit = true;
                    
                    // DO NOT clear the groups. We want to preserve cuts in the UI so the user can re-enable them!
                    // We only want to update the IsActive status to match Revit's current state.
                    
                    // Create a fast lookup for which cuts are active in the deserialized state
                    var activeCuts = new HashSet<string>();
                    foreach (var dg in deserializedGroups)
                    {
                        if (dg.IsActive)
                        {
                            foreach (var dc in dg.Clippings)
                            {
                                if (dc.IsActive) activeCuts.Add(dc.Name);
                            }
                        }
                    }

                    // Apply the active states to our in-memory groups
                    foreach (var g in groups)
                    {
                        // To be safe, we just check if any cuts in this group are active
                        bool groupHasActiveCuts = false;
                        foreach (var c in g.Clippings)
                        {
                            bool shouldBeActive = activeCuts.Contains(c.Name);
                            c.IsActive = shouldBeActive;
                            if (shouldBeActive) groupHasActiveCuts = true;
                        }
                        
                        // We leave the group active if it was active, or if it has active cuts
                        if (groupHasActiveCuts) g.IsActive = true;
                    }
                }
            }
            catch { }
            finally
            {
                UI.Views.ClippingManagerWindow.IsSyncingFromRevit = false;
            }
        }
    }
}
