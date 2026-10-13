using csharp2cbl;
using System;
using System.Collections.Generic;
using System.Text;
public class CobolDataItem
{
    public int Level { get; set; }
    public string Name { get; set; }
    public string Picture { get; set; }
    public string Usage { get; set; }
    public string Value { get; set; } = string.Empty;
    public int? Occurs { get; set; }
    public string Redefines { get; set; }
    public string DependsOn { get; set; }
    public List<string> Comments { get; set; } = new List<string>();
    public List<CobolDataItem> Children { get; set; } = new List<CobolDataItem>();
    public List<CobolConditionName> ConditionNames { get; set; } = new List<CobolConditionName>();

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"{Level:00} {Name}");
        if (!string.IsNullOrEmpty(Picture))
            sb.Append($" PIC {Picture}");
        if (!string.IsNullOrEmpty(Usage))
            sb.Append($" USAGE {Usage}");
        if (!string.IsNullOrEmpty(Value))
            sb.Append($" VALUE {Value}");
        if (Occurs.HasValue)
        {
            if (!string.IsNullOrEmpty(DependsOn))
                sb.Append($" OCCURS {Occurs.Value} TO {DependsOn} TIMES DEPENDING ON {DependsOn}");
            else
                sb.Append($" OCCURS {Occurs.Value} TIMES");
        }
        if (!string.IsNullOrEmpty(Redefines))
            sb.Append($" REDEFINES {Redefines}");
        return sb.ToString();
    }
}

public class CobolConditionName
{
    public string Name { get; set; }
    public string Value { get; set; }
    public string ThroughValue { get; set; }
}

public class CobolFileItem
{
    public string FileName { get; set; }
    public string ExternalName { get; set; }
    public string Organization { get; set; } = "LINE SEQUENTIAL";
    public string AccessMode { get; set; } = "SEQUENTIAL";
    public List<CobolDataItem> RecordFields { get; set; } = new List<CobolDataItem>();
}

public class CobolParagraph
{
    public string Name { get; set; }
    public List<string> Comments { get; set; } = new List<string>();
    public List<CobolDataItem> DataItems { get; set; } = new List<CobolDataItem>();
    public List<string> Statements { get; set; } = new List<string>();

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Name}.");
        foreach (var comment in Comments)
            sb.AppendLine($"*> {comment}");
        foreach (var dataItem in DataItems)
            sb.AppendLine(dataItem.ToString());
        foreach (var statement in Statements)
            sb.AppendLine(statement);
        return sb.ToString();
    }
}

public class CobolSection
{
    public string Name { get; set; }
    public List<CobolParagraph> Paragraphs { get; set; } = new List<CobolParagraph>();
}

public class CobolProgram
{
    public string ProgramId { get; set; }
    public List<string> Comments { get; set; } = new List<string>();
    public List<CobolDataItem> DataItems { get; set; } = new List<CobolDataItem>();
    public List<CobolFileItem> FileItems { get; set; } = new List<CobolFileItem>();
    public List<CobolParagraph> Paragraphs { get; set; } = new List<CobolParagraph>();
    public List<string> MainStatements { get; set; } = new List<string>();
    public List<string> InitializationStatements { get; set; } = new List<string>();
    public string Author { get; set; } = "CSharp2Cobol Transpiler";
    public string DateCompiled { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
}