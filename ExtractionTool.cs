// ExtractionTools.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ExtractorLib;

public static class ExtractionTools
{
    public static IEnumerable<(string name, string body)> ExtractClasses(string rel, string lang, string content)
    {
        // Pattern matching for class definitions in various languages
        var patterns = new Dictionary<string, string>
        {
            // C#, Java
            ["cs"] = @"(?:public|private|protected|internal|static|\s)+class\s+(\w+)",
            ["java"] = @"(?:public|private|protected|static|\s)+class\s+(\w+)",
            
            // TypeScript, JavaScript (ES6 classes)
            ["ts"] = @"(?:export\s+)?(?:abstract\s+)?class\s+(\w+)",
            ["tsx"] = @"(?:export\s+)?(?:abstract\s+)?class\s+(\w+)",
            ["js"] = @"class\s+(\w+)",
            ["jsx"] = @"class\s+(\w+)",
            
            // Python
            ["py"] = @"class\s+(\w+)",
            
            // Ruby
            ["rb"] = @"class\s+(\w+)",
            
            // Swift
            ["swift"] = @"(?:public|private|internal|open|fileprivate)?\s*class\s+(\w+)",
            
            // Kotlin
            ["kt"] = @"(?:data\s+)?(?:open|abstract|sealed)?\s*class\s+(\w+)",
            
            // PHP
            ["php"] = @"class\s+(\w+)",
            
            // Go (struct as class equivalent)
            ["go"] = @"type\s+(\w+)\s+struct",
            
            // Rust (struct as class equivalent)
            ["rs"] = @"(?:pub\s+)?struct\s+(\w+)",
        };

        if (!patterns.TryGetValue(lang, out var pattern))
            yield break;

        var regex = new Regex(pattern, RegexOptions.Multiline);
        var matches = regex.Matches(content);

        foreach (Match match in matches)
        {
            var className = match.Groups[1].Value;
            
            if (string.IsNullOrWhiteSpace(className))
                continue;

            // Extract class body
            var startIndex = match.Index;
            var endIndex = FindClassEnd(content, startIndex, lang);
            
            if (endIndex > startIndex)
            {
                var body = content.Substring(startIndex, endIndex - startIndex);
                if (body.Length > 10 && body.Length < 100000) // Skip very small or very large classes
                {
                    yield return (className, body);
                }
            }
        }
    }
    
    public static IEnumerable<(string name, string body)> ExtractFunctions(string rel, string lang, string content)
    {
        // Simple pattern matching for common function definitions
        var patterns = new Dictionary<string, string>
        {
            // C#, Java, C, C++, JavaScript, TypeScript
            ["cs"] = @"(?:public|private|protected|internal|static|\s)+(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["java"] = @"(?:public|private|protected|static|\s)+(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["c"] = @"(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["cpp"] = @"(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["js"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            ["ts"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            ["jsx"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            ["tsx"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            
            // Python
            ["py"] = @"def\s+(\w+)\s*\([^)]*\)\s*:",
            
            // Go
            ["go"] = @"func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)\s*\([^)]*\)",
            
            // Rust
            ["rs"] = @"fn\s+(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)",
            
            // PHP
            ["php"] = @"function\s+(\w+)\s*\([^)]*\)",
            
            // Ruby
            ["rb"] = @"def\s+(\w+)",
            
            // Swift
            ["swift"] = @"func\s+(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)",
        };

        if (!patterns.TryGetValue(lang, out var pattern))
            yield break;

        var regex = new Regex(pattern, RegexOptions.Multiline);
        var matches = regex.Matches(content);

        foreach (Match match in matches)
        {
            var functionName = match.Groups[1].Success ? match.Groups[1].Value : 
                              match.Groups[2].Success ? match.Groups[2].Value : null;
            
            if (string.IsNullOrWhiteSpace(functionName))
                continue;

            // Extract function body
            var startIndex = match.Index;
            var endIndex = FindFunctionEnd(content, startIndex, lang);
            
            if (endIndex > startIndex)
            {
                var body = content.Substring(startIndex, endIndex - startIndex);
                if (body.Length > 10 && body.Length < 50000)
                {
                    yield return (functionName, body);
                }
            }
        }
    }

    static int FindClassEnd(string content, int startIndex, string lang)
    {
        // For brace-based languages
        if (new[] { "cs", "java", "ts", "tsx", "js", "jsx", "php", "swift", "kt", "go", "rs" }.Contains(lang))
        {
            var braceCount = 0;
            var foundOpenBrace = false;
            
            for (int i = startIndex; i < content.Length; i++)
            {
                if (content[i] == '{')
                {
                    braceCount++;
                    foundOpenBrace = true;
                }
                else if (content[i] == '}')
                {
                    braceCount--;
                    if (foundOpenBrace && braceCount == 0)
                    {
                        return i + 1;
                    }
                }
            }
        }
        // For Python/Ruby (indentation-based)
        else if (lang == "py" || lang == "rb")
        {
            var lines = content.Substring(startIndex).Split('\n');
            if (lines.Length == 0) return startIndex + 100;
            
            var firstLine = lines[0];
            var baseIndent = firstLine.Length - firstLine.TrimStart().Length;
            
            var bodyLength = firstLine.Length;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    bodyLength += line.Length + 1;
                    continue;
                }
                
                var indent = line.Length - line.TrimStart().Length;
                if (indent <= baseIndent && line.TrimStart().StartsWith("class "))
                {
                    break;
                }
                
                bodyLength += line.Length + 1;
                
                if (bodyLength > 50000) break;
            }
            
            return Math.Min(startIndex + bodyLength, content.Length);
        }
        
        return Math.Min(startIndex + 5000, content.Length);
    }

    static int FindFunctionEnd(string content, int startIndex, string lang)
    {
        // For brace-based languages
        if (new[] { "cs", "java", "c", "cpp", "js", "ts", "jsx", "tsx", "go", "rs", "php", "swift" }.Contains(lang))
        {
            var braceCount = 0;
            var foundOpenBrace = false;
            
            for (int i = startIndex; i < content.Length; i++)
            {
                if (content[i] == '{')
                {
                    braceCount++;
                    foundOpenBrace = true;
                }
                else if (content[i] == '}')
                {
                    braceCount--;
                    if (foundOpenBrace && braceCount == 0)
                    {
                        return i + 1;
                    }
                }
            }
        }
        // For Python/Ruby
        else if (lang == "py" || lang == "rb")
        {
            var lines = content.Substring(startIndex).Split('\n');
            if (lines.Length == 0) return startIndex + 100;
            
            var firstLine = lines[0];
            var baseIndent = firstLine.Length - firstLine.TrimStart().Length;
            
            var bodyLength = firstLine.Length;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    bodyLength += line.Length + 1;
                    continue;
                }
                
                var indent = line.Length - line.TrimStart().Length;
                if (indent <= baseIndent && line.TrimStart().StartsWith("def "))
                {
                    break;
                }
                
                bodyLength += line.Length + 1;
                
                if (bodyLength > 10000) break;
            }
            
            return Math.Min(startIndex + bodyLength, content.Length);
        }
        
        return Math.Min(startIndex + 500, content.Length);
    }

    public static string HeuristicClassTitle(string className, string rel, string lang, string body)
    {
        var memberCount = 0;
        var hasConstructor = false;
        
        var methodPattern = lang switch
        {
            "cs" or "java" => @"(?:public|private|protected|internal|static|\s)+(?:\w+\s+)+\w+\s*\([^)]*\)",
            "py" => @"def\s+\w+\s*\(",
            "js" or "ts" or "jsx" or "tsx" => @"(?:async\s+)?\w+\s*\([^)]*\)\s*{",
            _ => null
        };
        
        if (methodPattern != null)
        {
            memberCount = Regex.Matches(body, methodPattern).Count;
        }
        
        hasConstructor = lang switch
        {
            "cs" or "java" => body.Contains($"public {className}(") || body.Contains($"private {className}(") || body.Contains($"protected {className}("),
            "py" => body.Contains("def __init__"),
            "js" or "ts" or "jsx" or "tsx" => body.Contains("constructor("),
            "swift" => body.Contains("init("),
            "kt" => body.Contains("constructor(") || body.Contains($"class {className}("),
            _ => false
        };
        
        var parts = new List<string> { className };
        if (memberCount > 0) parts.Add($"{memberCount} members");
        if (hasConstructor) parts.Add("constructor");
        parts.Add($"{System.IO.Path.GetFileName(rel)} {lang}");
        
        return string.Join(" • ", parts);
    }

    public static string HeuristicFunctionTitle(string fn, string rel, string lang, string body)
    {
        var verbs = Regex.Match(body, @"\b(get|set|build|create|update|delete|fetch|render|compute|calculate|process|handle|validate)\b", RegexOptions.IgnoreCase);
        var hint = verbs.Success ? verbs.Value.ToLowerInvariant() : "function";
        return $"{fn} — {hint} ({System.IO.Path.GetFileName(rel)} {lang})";
    }

    public static string HeuristicFileTitle(string rel, string lang, string content)
    {
        var tokens = new List<string> { System.IO.Path.GetFileName(rel), lang };
        var hints = ExtractTopNIdentifiers(content, 5);
        if (hints.Count > 0) tokens.Add(string.Join("/", hints));
        return string.Join(" • ", tokens);
    }

    public static List<string> ExtractTopNIdentifiers(string content, int n)
    {
        var rx = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]{3,})\b");
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in rx.Matches(content))
        {
            var t = m.Groups[1].Value;
            if (IsNoise(t)) continue;
            freq[t] = freq.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        return freq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(n).Select(kv => kv.Key).ToList();
    }

    static bool IsNoise(string t)
    {
        var k = t.ToLowerInvariant();
        return k is "the" or "and" or "else" or "null" or "true" or "false" or "class" or "public" or "private" or "protected" or "static" or "void" or "int" or "string" or "return" or "if" or "for" or "while" or "this" or "var" or "let" or "const" or "function";
    }

    public static string Sha256(string s)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}