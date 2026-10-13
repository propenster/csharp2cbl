using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;


public static class Mapper
{
    // Syntax-based fallback (used when SemanticModel is unavailable)
    public static string MapType(TypeSyntax type)
    {
        if (type == null) return "PIC X(255)";
        string typeName = type.ToString();
        return MapTypeName(typeName);
    }

    // Semantic model-based (preferred — resolves var, aliases, usings)
    public static string MapType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null) return "PIC X(255)";

        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            string elementPic = MapType(arrayType.ElementType);
            // Arrays are handled at the data item level with OCCURS
            return elementPic;
        }

        if (typeSymbol.SpecialType != SpecialType.None)
        {
            return MapSpecialType(typeSymbol.SpecialType);
        }

        // Handle nullable types
        if (typeSymbol.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T)
        {
            var namedType = typeSymbol as INamedTypeSymbol;
            if (namedType != null && namedType.TypeArguments.Length > 0)
            {
                return MapType(namedType.TypeArguments[0]);
            }
        }

        // Handle enums
        if (typeSymbol.TypeKind == TypeKind.Enum)
        {
            return "PIC 9(9)";
        }

        // String-ish types
        if (typeSymbol.Name == "StringBuilder" || typeSymbol.Name == "String")
        {
            return "PIC X(1000)";
        }

        if (typeSymbol.Name == "DateTime" || typeSymbol.Name == "TimeSpan")
        {
            return "PIC X(26)";
        }

        if (typeSymbol.Name == "Guid")
        {
            return "PIC X(36)";
        }

        return "PIC X(255)";
    }

    private static string MapSpecialType(SpecialType specialType)
    {
        return specialType switch
        {
            SpecialType.System_Int32 => "PIC S9(9)",
            SpecialType.System_Int64 => "PIC S9(18)",
            SpecialType.System_Int16 => "PIC S9(4)",
            SpecialType.System_String => "PIC X(255)",
            SpecialType.System_Boolean => "PIC 9",
            SpecialType.System_Decimal => "PIC S9(13)V9(2)",
            SpecialType.System_Single => "COMP-1",
            SpecialType.System_Double => "COMP-2",
            SpecialType.System_Char => "PIC X(1)",
            SpecialType.System_Byte => "PIC 9(3)",
            SpecialType.System_SByte => "PIC S9(3)",
            SpecialType.System_UInt32 => "PIC 9(9)",
            SpecialType.System_UInt64 => "PIC 9(18)",
            SpecialType.System_UInt16 => "PIC 9(4)",
            SpecialType.System_Object => "PIC X(255)",
            SpecialType.System_Void => "",
            _ => "PIC X(255)"
        };
    }

    private static string MapTypeName(string typeName)
    {
        return typeName switch
        {
            "int" => "PIC S9(9)",
            "long" => "PIC S9(18)",
            "short" => "PIC S9(4)",
            "string" => "PIC X(255)",
            "bool" => "PIC 9",
            "decimal" => "PIC S9(13)V9(2)",
            "float" => "COMP-1",
            "double" => "COMP-2",
            "char" => "PIC X(1)",
            "byte" => "PIC 9(3)",
            "sbyte" => "PIC S9(3)",
            "uint" => "PIC 9(9)",
            "ulong" => "PIC 9(18)",
            "ushort" => "PIC 9(4)",
            "object" => "PIC X(255)",
            "var" => "PIC X(255)", // Should never hit this when semantic model is used
            _ => "PIC X(255)"
        };
    }

    public static string MapLiteral(LiteralExpressionSyntax literal)
    {
        return literal.Token.Value switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            short s => s.ToString(),
            string str => $"'{str.Replace("'", "''")}'", // COBOL uses single quotes
            bool b => b ? "1" : "0",
            decimal d => d.ToString(),
            float f => f.ToString(),
            double db => db.ToString(),
            char c => $"'{c}'",
            byte by => by.ToString(),
            null => "NULL", // Will need special handling
            _ => literal.Token.ValueText
        };
    }

    public static string MapOperator(string csharpOp)
    {
        return csharpOp switch
        {
            "==" => "=",
            "!=" => "<>",
            ">" => ">",
            "<" => "<",
            ">=" => ">=",
            "<=" => "<=",
            "+" => "+",
            "-" => "-",
            "*" => "*",
            "/" => "/",
            "%" => "FUNCTION MOD",
            "||" => "OR",
            "&&" => "AND",
            "!" => "NOT",
            "&" => "B-AND", // Non-standard, may need FUNCTION
            "|" => "B-OR",
            "^" => "B-XOR",
            "<<" => "*", // Approximation
            ">>" => "/", // Approximation
            _ => csharpOp
        };
    }

    public static string SanitizeProgramId(string name)
    {
        if (string.IsNullOrEmpty(name)) return "CSHARP2CBL";
        string sanitized = Regex.Replace(name.ToUpperInvariant(), @"[^A-Z0-9]", "");
        if (sanitized.Length > 30) sanitized = sanitized.Substring(0, 30);
        if (sanitized.Length == 0) return "CSHARP2CBL";
        if (char.IsDigit(sanitized[0])) sanitized = "P" + sanitized;
        return sanitized;
    }

    public static string SanitizeParagraphName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "P-MAIN";
        string separated = Regex.Replace(name, @"([a-z])([A-Z])", "$1-$2");
        string sanitized = Regex.Replace(separated.ToUpperInvariant(), @"[^A-Z0-9\-]", "-");
        sanitized = Regex.Replace(sanitized, @"-+", "-").Trim('-');
        if (sanitized.Length > 30) sanitized = sanitized.Substring(0, 30);
        if (sanitized.Length == 0) return "P-MAIN";
        if (char.IsDigit(sanitized[0])) sanitized = "P-" + sanitized;
        return sanitized;
    }

    public static string SanitizeDataName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "WS-ITEM";
        string separated = Regex.Replace(name, @"([a-z])([A-Z])", "$1-$2");
        string sanitized = Regex.Replace(separated.ToUpperInvariant(), @"[^A-Z0-9\-]", "-");
        sanitized = Regex.Replace(sanitized, @"-+", "-").Trim('-');
        if (sanitized.Length > 30) sanitized = sanitized.Substring(0, 30);
        if (sanitized.Length == 0) return "WS-ITEM";
        if (char.IsDigit(sanitized[0])) sanitized = "WS-" + sanitized;
        if (!sanitized.StartsWith("WS-") && !sanitized.StartsWith("LS-") && !sanitized.StartsWith("FS-"))
            sanitized = "WS-" + sanitized;
        return sanitized;
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "FD-FILE";
        string sanitized = Regex.Replace(name.ToUpperInvariant(), @"[^A-Z0-9\-]", "-");
        sanitized = Regex.Replace(sanitized, @"-+", "-").Trim('-');
        if (sanitized.Length > 30) sanitized = sanitized.Substring(0, 30);
        if (sanitized.Length == 0) return "FD-FILE";
        if (char.IsDigit(sanitized[0])) sanitized = "FD-" + sanitized;
        if (!sanitized.StartsWith("FD-")) sanitized = "FD-" + sanitized;
        return sanitized;
    }
}

