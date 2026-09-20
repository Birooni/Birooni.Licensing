using System;
using System.Collections.Generic;

namespace FamilyLoader
{
    public class Configuration
    {
        // New hierarchy
        public List<TabConfig> Tabs { get; set; } = new List<TabConfig>();

        // Legacy property for backward compatibility during deserialization
        public List<PanelConfig> Sessions { get; set; } = new List<PanelConfig>();
    }

    public class TabConfig
    {
        public string Name { get; set; } = string.Empty;
        public List<PanelConfig> Panels { get; set; } = new List<PanelConfig>();
    }

    public class PanelConfig
    {
        public string Name { get; set; } = string.Empty;
        public List<FamilyConfig> Families { get; set; } = new List<FamilyConfig>();
    }

    public class FamilyConfig
    {
        public string RfaPath { get; set; } = string.Empty;
        public string FamilyName { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public bool IsIconOnly { get; set; } = false;
        public bool IsTypesAsPushButtons { get; set; } = false;
        public int StackRows { get; set; } = 1;
        public List<TypeConfig> Types { get; set; } = new List<TypeConfig>();
    }

    public class TypeConfig
    {
        public string TypeName { get; set; } = string.Empty;
        public string CustomName { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public bool IsIconOnly { get; set; } = false;
    }
}
