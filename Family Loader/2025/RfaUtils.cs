using System;
using System.Collections.Generic;

namespace FamilyLoader
{
    public static class RfaUtils
    {
        public static List<string> GetFamilyTypesFast(string rfaPath)
        {
            List<string> types = new List<string>();
            try
            {
                byte[] data;
                using (var fs = new System.IO.FileStream(rfaPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    using (OpenMcdf.CompoundFile cf = new OpenMcdf.CompoundFile(fs))
                    {
                        OpenMcdf.CFStream partAtomStream = cf.RootStorage.GetStream("PartAtom");
                        data = partAtomStream.GetData();
                    }
                }

                if (data != null && data.Length > 0)
                {
                    using (var ms = new System.IO.MemoryStream(data))
                    {
                        var xmlDoc = System.Xml.Linq.XDocument.Load(ms);
                        System.Xml.Linq.XNamespace a = "http://www.w3.org/2005/Atom";
                        System.Xml.Linq.XNamespace adsk = "urn:schemas-autodesk-com:partatom";
                        
                        if (xmlDoc.Root == null) return types;
                        var parts = xmlDoc.Root.Descendants(adsk + "part");
                        foreach (var part in parts)
                        {
                            var titleElem = part.Element(a + "title") ?? part.Element("title");
                            if (titleElem != null && !string.IsNullOrWhiteSpace(titleElem.Value))
                            {
                                types.Add(titleElem.Value);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore, will fallback to empty list if not an RFA or no PartAtom stream
            }
            return types;
        }
    }
}
