using csharp2cbl;
using System.Text;

public class CobolEmitter
{
    private const int AreaA = 8;   // Columns 8-11
    private const int AreaB = 12;  // Columns 12-71
    private const int MaxLine = 72;
    private const int SequenceStart = 1;
    private const int SequenceEnd = 6;

    private readonly string _dialect;
    private int _sequenceNumber = 1;

    public CobolEmitter(string dialect = "zcobol")
    {
        _dialect = dialect.ToLowerInvariant();
    }

    public string Emit(CobolProgram program)
    {
        var sb = new StringBuilder();

        // Sequence numbers area (optional, but good for mainframe compatibility)
        // For zCOBOL we can leave it blank or use line numbers

        // COMMENTS / IDENTIFICATION
        foreach (var comment in program.Comments)
        {
            EmitComment(sb, comment);
        }

        // IDENTIFICATION DIVISION
        EmitLine(sb, "IDENTIFICATION DIVISION.", AreaA);
        EmitLine(sb, $"PROGRAM-ID. {program.ProgramId}.", AreaA);
        if (!string.IsNullOrEmpty(program.Author))
            EmitLine(sb, $"AUTHOR. {program.Author}.", AreaB);
        if (!string.IsNullOrEmpty(program.DateCompiled))
            EmitLine(sb, $"DATE-COMPILED. {program.DateCompiled}.", AreaB);

        // ENVIRONMENT DIVISION
        EmitLine(sb, "ENVIRONMENT DIVISION.", AreaA);
        EmitLine(sb, "CONFIGURATION SECTION.", AreaA);
        EmitLine(sb, "SOURCE-COMPUTER. IBM-370.", AreaB);
        EmitLine(sb, "OBJECT-COMPUTER. IBM-370.", AreaB);

        // INPUT-OUTPUT SECTION (if files exist)
        if (program.FileItems.Any())
        {
            EmitLine(sb, "INPUT-OUTPUT SECTION.", AreaA);
            EmitLine(sb, "FILE-CONTROL.", AreaA);
            foreach (var file in program.FileItems)
            {
                EmitLine(sb, $"    SELECT {file.FileName} ASSIGN TO '{file.ExternalName}'", AreaB);
                EmitLine(sb, $"        ORGANIZATION IS {file.Organization}", AreaB);
                EmitLine(sb, $"        ACCESS MODE IS {file.AccessMode}.", AreaB);
            }
        }

        // DATA DIVISION
        EmitLine(sb, "DATA DIVISION.", AreaA);

        // FILE SECTION
        if (program.FileItems.Any())
        {
            EmitLine(sb, "FILE SECTION.", AreaA);
            foreach (var file in program.FileItems)
            {
                EmitLine(sb, $"FD  {file.FileName}.", AreaA);
                foreach (var field in file.RecordFields)
                {
                    EmitDataItem(sb, field);
                }
            }
        }

        // WORKING-STORAGE SECTION
        EmitLine(sb, "WORKING-STORAGE SECTION.", AreaA);

        foreach (var dataItem in program.DataItems)
        {
            EmitDataItem(sb, dataItem);
        }

        // LOCAL-STORAGE SECTION (for recursive / reentrant methods)
        // Skipped for v1.0.0 unless dialect supports it

        // LINKAGE SECTION (for CALL parameters)
        // Skipped for v1.0.0

        // PROCEDURE DIVISION
        // PROCEDURE DIVISION
        EmitLine(sb, "PROCEDURE DIVISION.", AreaA);

        // Initialization paragraph (constructors, field initializers)
        if (program.InitializationStatements.Any())
        {
            EmitLine(sb, "INIT-PROGRAM.", AreaA);
            foreach (var stmt in program.InitializationStatements)
            {
                EmitStatement(sb, stmt);
            }
        }

        // MAIN paragraph
        if (program.MainStatements.Any())
        {
            EmitLine(sb, "MAIN.", AreaA);
            if (program.InitializationStatements.Any())
                EmitLine(sb, "PERFORM INIT-PROGRAM", AreaB);

            foreach (var stmt in program.MainStatements)
            {
                EmitStatement(sb, stmt);
            }
        }

        // Other paragraphs
        foreach (var paragraph in program.Paragraphs)
        {
            if (paragraph.Name == "MAIN" && program.MainStatements.Any())
                continue; // Already emitted

            EmitLine(sb, $"{paragraph.Name}.", AreaA);
            foreach (var comment in paragraph.Comments)
            {
                EmitComment(sb, comment);
            }
            foreach (var stmt in paragraph.Statements)
            {
                EmitStatement(sb, stmt);
            }
        }

        // Program end
        EmitLine(sb, "END PROGRAM " + program.ProgramId + ".", AreaA);

        return sb.ToString();
    }

    private void EmitDataItem(StringBuilder sb, CobolDataItem item, int indentLevel = 0)
    {
        foreach (var comment in item.Comments)
        {
            EmitComment(sb, comment);
        }

        int startCol = indentLevel > 0 ? AreaB + (indentLevel * 4) : AreaA;
        if (startCol > AreaB + 12) startCol = AreaB + 12; // Cap indentation

        string indent = new string(' ', startCol);
        string line = $"{indent}{item.Level:00}  {item.Name}";

        if (!string.IsNullOrEmpty(item.Picture))
        {
            // Mapper returns either a full picture clause ("PIC S9(9)") or a
            // usage-only clause ("COMP-1"). Emit both correctly.
            if (item.Picture.StartsWith("PIC ", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("COMP", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("BINARY", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("PACKED", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("POINTER", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase) ||
                item.Picture.StartsWith("NATIONAL", StringComparison.OrdinalIgnoreCase))
            {
                line += $" {item.Picture}";
            }
            else
            {
                line += $" PIC {item.Picture}";
            }
        }

        if (!string.IsNullOrEmpty(item.Usage))
        {
            line += $" USAGE {item.Usage}";
        }

        if (!string.IsNullOrEmpty(item.Value))
        {
            line += $" VALUE {item.Value}";
        }

        if (item.Occurs.HasValue)
        {
            if (!string.IsNullOrEmpty(item.DependsOn))
                line += $" OCCURS {item.Occurs.Value} TO {item.DependsOn} TIMES DEPENDING ON {item.DependsOn}";
            else
                line += $" OCCURS {item.Occurs.Value} TIMES";
        }

        if (!string.IsNullOrEmpty(item.Redefines))
        {
            line += $" REDEFINES {item.Redefines}";
        }

        line += ".";
        EmitLineRaw(sb, line);

        // 88-level condition names
        foreach (var cond in item.ConditionNames)
        {
            string condLine = $"{new string(' ', startCol + 4)}88  {cond.Name}";
            if (!string.IsNullOrEmpty(cond.ThroughValue))
                condLine += $" VALUE {cond.Value} THRU {cond.ThroughValue}";
            else
                condLine += $" VALUE {cond.Value}";
            condLine += ".";
            EmitLineRaw(sb, condLine);
        }

        foreach (var child in item.Children)
        {
            EmitDataItem(sb, child, indentLevel + 1);
        }
    }

    private void EmitStatement(StringBuilder sb, string statement)
    {
        if (string.IsNullOrEmpty(statement)) return;

        // Statements are already formatted with their own logical line breaks
        // (nested IF / STRING / etc. use "\n"). Split on newlines only.
        var lines = statement.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string trimmedStart = line.TrimStart();
            int leadingSpaces = line.Length - trimmedStart.Length;
            int startCol = AreaB + Math.Min(leadingSpaces, 20);

            EmitLine(sb, trimmedStart.TrimEnd(), startCol);
        }
    }

    private void EmitComment(StringBuilder sb, string text)
    {
        // COBOL comments start in column 1 with * or *> for inline
        string comment = $"*> {text}";
        EmitLine(sb, comment, AreaA);
    }

    //private void EmitLine(StringBuilder sb, string text, int startColumn)
    //{
    //    string indent = new string(' ', startColumn);

    //    if (text.Length + startColumn > MaxLine)
    //    {
    //        // Word wrap with continuation lines
    //        int available = MaxLine - startColumn;
    //        int pos = 0;
    //        bool firstLine = true;

    //        while (pos < text.Length)
    //        {
    //            int length = Math.Min(available, text.Length - pos);
    //            // Try to break at a space
    //            if (!firstLine && length < text.Length - pos)
    //            {
    //                int lastSpace = text.LastIndexOf(' ', pos + length - 1, length);
    //                if (lastSpace > pos)
    //                {
    //                    length = lastSpace - pos;
    //                }
    //            }

    //            string segment = text.Substring(pos, length).TrimEnd();

    //            if (!firstLine)
    //            {
    //                // Continuation: hyphen in column 7, text from column 8
    //                sb.AppendLine($"      -    {segment.TrimStart()}");
    //            }
    //            else
    //            {
    //                sb.AppendLine($"{indent}{segment}");
    //                firstLine = false;
    //                available = MaxLine - 8; // Continuation starts at column 8
    //            }
    //            pos += length;
    //            // Skip leading space for next segment
    //            while (pos < text.Length && text[pos] == ' ') pos++;
    //        }
    //    }
    //    else
    //    {
    //        sb.AppendLine($"{indent}{text}");
    //    }
    //}

    //private void EmitLineRaw(StringBuilder sb, string text)
    //{
    //    if (text.Length > MaxLine)
    //    {
    //        EmitLine(sb, text.TrimStart(), text.Length - text.TrimStart().Length);
    //    }
    //    else
    //    {
    //        sb.AppendLine(text);
    //    }
    //}
    private void EmitLine(StringBuilder sb, string text, int startColumn)
    {
        string indent = new string(' ', startColumn);

        if (text.Length + startColumn > MaxLine)
        {
            // Word wrap with continuation lines
            int available = MaxLine - startColumn;
            int pos = 0;
            bool firstLine = true;

            while (pos < text.Length)
            {
                int length = Math.Min(available, text.Length - pos);

                // Try to break at a space for ALL lines to prevent cutting words in half
                if (length < text.Length - pos)
                {
                    int lastSpace = text.LastIndexOf(' ', pos + length - 1, length);
                    if (lastSpace > pos)
                    {
                        length = lastSpace - pos;
                    }
                }

                string segment = text.Substring(pos, length).TrimEnd();

                if (!firstLine)
                {
                    // Continuation: 6 spaces, hyphen in column 7, 4 spaces to reach Area B (column 12)
                    sb.AppendLine($"      -    {segment.TrimStart()}");
                }
                else
                {
                    sb.AppendLine($"{indent}{segment}");
                    firstLine = false;

                    // Continuation prefix "      -    " takes up exactly 11 characters
                    available = MaxLine - 11;
                }

                pos += length;

                // Skip leading spaces for the next segment so they don't consume Area B width
                while (pos < text.Length && text[pos] == ' ')
                {
                    pos++;
                }
            }
        }
        else
        {
            sb.AppendLine($"{indent}{text}");
        }
    }

    private void EmitLineRaw(StringBuilder sb, string text)
    {
        if (text.Length > MaxLine)
        {
            int spacesAtStart = text.Length - text.TrimStart().Length;
            EmitLine(sb, text.TrimStart(), spacesAtStart);
        }
        else
        {
            sb.AppendLine(text);
        }
    }
}