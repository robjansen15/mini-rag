using System;
using System.Collections.Generic;
using System.IO;

namespace ExtractorLib.Internal;

static class FileRules
{
    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",".ipynb",".js",".mjs",".cjs",".ts",".tsx",".jsx",".vue",".svelte",".java",".kt",".kts",".scala",".go",".rs",
        ".c",".h",".cpp",".cc",".cxx",".hpp",".hh",".m",".mm",".cs",".fs",".fsx",".php",".rb",".swift",".lua",".pl",".pm",".r",
        ".dart",".groovy",".gradle",".sql",".proto",".graphql",".gql",
        ".json",".json5",".toml",".ini",".cfg",".conf",".yaml",".yml",".env",".properties",".xml",
        ".html",".htm",".css",".scss",".sass",".less",
        ".md",".markdown",".rst",".adoc",".txt",".csv",".tsv",".log",".org",
    };

    static readonly Dictionary<string, string> SpecialBasenames = new(StringComparer.Ordinal)
    {
        ["Dockerfile"]="dockerfile",["Makefile"]="make",["CMakeLists.txt"]="cmake",
        ["BUILD"]="bazel",["WORKSPACE"]="bazel",["Podfile"]="cocoapods",["Gemfile"]="ruby-gems",
        ["requirements.txt"]="python-reqs",["environment.yml"]="conda-env",["Pipfile"]="pipenv",["Pipfile.lock"]="pipenv-lock",
        ["package.json"]="npm",["pnpm-lock.yaml"]="pnpm-lock",["yarn.lock"]="yarn-lock",["poetry.lock"]="poetry-lock",["pyproject.toml"]="pyproject",
        ["Cargo.toml"]="cargo",["Cargo.lock"]="cargo-lock",["go.mod"]="gomod",["go.sum"]="gosum",
        ["composer.json"]="composer",["composer.lock"]="composer-lock",["pom.xml"]="maven",
        ["build.gradle.kts"]="gradle-kts",["build.gradle"]="gradle",
        [".gitignore"]="git",[".gitattributes"]="git",[".editorconfig"]="editor",
    };

    static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",".hg",".svn",".bzr","node_modules","dist","build","out","target",
        ".idea",".vscode",".vs","__pycache__",".venv","venv",".mypy_cache",".pytest_cache",".gradle",".next",".nuxt",".parcel-cache"
    };

    public static bool IsAllowedFile(string path)
    {
        var name = Path.GetFileName(path);
        if (SpecialBasenames.ContainsKey(name))
        {
            return true;
        }

        var ext = Path.GetExtension(name);
        return ext.Length > 0 && AllowedExtensions.Contains(ext);
    }

    public static bool IsIgnoredDirectory(string dirName)
        => IgnoredDirectories.Contains(dirName);

    public static string DetectLanguage(string path)
    {
        var name = Path.GetFileName(path);
        if (SpecialBasenames.TryGetValue(name, out var hint))
        {
            return hint;
        }

        var ext = Path.GetExtension(path);
        return ext.Length > 0
            ? ext.TrimStart('.').ToLowerInvariant()
            : "text";
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        return normalized;
    }

    public static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            {
                continue;
            }
            chars[i] = '_';
        }
        return new string(chars);
    }
}
