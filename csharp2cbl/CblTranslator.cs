using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public class CblTranslator
{
    private readonly string _dialect;

    // Prepended to every compilation unit so user code that omits
    // these usings still resolves BCL types like Console, List<T>, etc.
    private const string ImplicitUsings =
        "using System;\n" +
        "using System.Collections;\n" +
        "using System.Collections.Generic;\n" +
        "using System.IO;\n" +
        "using System.Linq;\n" +
        "using System.Text;\n" +
        "using System.Threading;\n" +
        "using System.Threading.Tasks;\n";

    public CblTranslator(string dialect = "zcobol")
    {
        _dialect = dialect;
    }

    public string Translate(string csharpSource, string programId = null)
    {
        // Prepend usings so the compilation sees the BCL even when the
        // caller's source does not include `using System;`.
        string sourceWithUsings = ImplicitUsings + csharpSource;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            sourceWithUsings,
            new CSharpParseOptions(LanguageVersion.Latest));

        // Reference assemblies: use the whole TPA----------
        var references = new List<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch { }
            }
        }
        if (references.Count == 0)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Console).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
        }

        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        // Detect entry point to choose the right OutputKind
        bool hasGlobalStatements = root.Members.OfType<GlobalStatementSyntax>().Any();
        bool hasMainMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "Main");
        bool hasEntryPoint = hasGlobalStatements || hasMainMethod;

        var options = new CSharpCompilationOptions(
            hasEntryPoint ? OutputKind.ConsoleApplication
                          : OutputKind.DynamicallyLinkedLibrary);

        var compilation = CSharpCompilation.Create(
            "CSharp2CobolTemp",
            syntaxTrees: new[] { tree },
            references: references,
            options: options);

        var diagnostics = tree.GetDiagnostics()
            .Concat(compilation.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (diagnostics.Any())
        {
            var errorMessages = string.Join("", diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"C# compilation errors detected: {errorMessages}");
        }

        SemanticModel semanticModel = compilation.GetSemanticModel(tree);

        var parser = new Parser(semanticModel);
        var cobolProgram = parser.Parse(root, programId);

        var emitter = new CobolEmitter(_dialect);
        return emitter.Emit(cobolProgram);
    }
}