namespace ExtractorLib.Internal;

static class CorpusIdFactory
{
    public static string ForFile(string relativePath)
    {
        var normalized = FileRules.NormalizeRelativePath(relativePath);
        return string.IsNullOrEmpty(normalized) ? "root.file" : normalized + ".file";
    }

    public static string ForClass(string relativePath, string className)
    {
        var normalized = FileRules.NormalizeRelativePath(relativePath);
        var safeName = FileRules.SanitizeIdentifier(className);
        return string.IsNullOrEmpty(normalized)
            ? $"{safeName}.class"
            : $"{normalized}.{safeName}.class";
    }

    public static string ForFunction(string relativePath, string functionName)
    {
        var normalized = FileRules.NormalizeRelativePath(relativePath);
        var safeName = FileRules.SanitizeIdentifier(functionName);
        return string.IsNullOrEmpty(normalized)
            ? $"{safeName}.function"
            : $"{normalized}.{safeName}.function";
    }
}
