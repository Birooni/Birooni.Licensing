using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BiruBox.Core;

/// <summary>
/// Tracks session-specific state, such as whether a document has been initialized in the current Revit session.
/// </summary>
public static class SessionManager
{
    private static readonly HashSet<string> InitializedDocuments = new();

    public static bool IsDocumentInitialized(Document? doc)
    {
        if (doc == null) return true;
        string key = GetDocKey(doc);
        return InitializedDocuments.Contains(key);
    }

    public static void MarkDocumentInitialized(Document? doc)
    {
        if (doc == null) return;
        string key = GetDocKey(doc);
        InitializedDocuments.Add(key);
    }

    private static string GetDocKey(Document doc)
    {
        if (!string.IsNullOrEmpty(doc.PathName))
            return doc.PathName;

        // For unsaved documents, combine Title with project GUID to disambiguate
        try
        {
            var guid = doc.ProjectInformation?.UniqueId ?? string.Empty;
            return doc.Title + "|" + guid;
        }
        catch
        {
            return doc.Title;
        }
    }
}
