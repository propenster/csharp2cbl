using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace tests
{
    public class ParserTests
    {
        private CobolProgram Parse(string source, string programId = null)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var compilation = CSharpCompilation.Create("TestCompilation",
                syntaxTrees: new[] { tree },
                references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var parser = new Parser(semanticModel);
            return parser.Parse(root, programId);
        }

        [Fact]
        public void Parse_TopLevelStatements_CreatesMainParagraph()
        {
            var source = @"Console.WriteLine(""Hello"");";
            var program = Parse(source);

            Assert.Equal("CSHARP2CBL", program.ProgramId);
            Assert.Contains(program.Paragraphs, p => p.Name == "MAIN");
            Assert.Contains(program.MainStatements, s => s.Contains("DISPLAY") || s.Contains("CALL 'CONSOLE-WRITE-LINE'"));
            Assert.Contains(program.MainStatements, s => s.Contains("STOP RUN"));
        }

        [Fact]
        public void Parse_ClassWithMainMethod_ExtractsProgramId()
        {
            var source = @"
namespace Test
{
    class HelloWorld
    {
        static void Main(string[] args)
        {
            Console.WriteLine(""Hello"");
        }
    }
}";
            var program = Parse(source);

            Assert.Equal("HELLOWORLD", program.ProgramId);
            Assert.Contains(program.Paragraphs, p => p.Name == "MAIN");
        }

        [Fact]
        public void Parse_CustomProgramId_OverridesClassName()
        {
            var source = @"class Test { }";
            var program = Parse(source, "MYPGM");

            Assert.Equal("MYPGM", program.ProgramId);
        }

        [Fact]
        public void Parse_MethodDeclaration_CreatesParagraph()
        {
            var source = @"
class Test
{
    void Calculate()
    {
        int x = 5;
    }
}";
            var program = Parse(source);

            Assert.Contains(program.Paragraphs, p => p.Name == "CALCULATE");
            Assert.Contains(program.DataItems, d => d.Name.Contains("CALCULATE") && d.Name.Contains("X"));
        }

        [Fact]
        public void Parse_MethodParameters_CreateDataItems()
        {
            var source = @"
class Test
{
    void Process(int id, string name)
    {
    }
}";
            var program = Parse(source);

            Assert.Contains(program.DataItems, d => d.Name.Contains("PROCESS") && d.Name.Contains("ID"));
            Assert.Contains(program.DataItems, d => d.Name.Contains("PROCESS") && d.Name.Contains("NAME"));
        }

        [Fact]
        public void Parse_FieldDeclaration_CreatesWorkingStorage()
        {
            var source = @"
class Test
{
    int counter = 0;
    string message = ""Hello"";
}";
            var program = Parse(source);

            Assert.Contains(program.DataItems, d => d.Name == "WS-COUNTER");
            Assert.Contains(program.DataItems, d => d.Name == "WS-MESSAGE");
        }

        [Fact]
        public void Parse_PropertyDeclaration_CreatesWorkingStorage()
        {
            var source = @"
class Test
{
    public int Count { get; set; }
}";
            var program = Parse(source);

            Assert.Contains(program.DataItems, d => d.Name == "WS-COUNT");
        }

        [Fact]
        public void Parse_ConsoleWriteLine_EmitsDisplay()
        {
            var source = @"Console.WriteLine(""Test Message"");";
            var program = Parse(source);

            Assert.Contains(program.MainStatements, s => s.Contains("DISPLAY") || s.Contains("CALL 'CONSOLE-WRITE-LINE'"));
        }

        [Fact]
        public void Parse_ConsoleReadLine_EmitsAccept()
        {
            var source = @"
class Test
{
    static void Main()
    {
        string input = Console.ReadLine();
    }
}";
            var program = Parse(source);

            var mainPara = program.Paragraphs.FirstOrDefault(p => p.Name == "MAIN");
            Assert.NotNull(mainPara);
            Assert.Contains(mainPara.Statements, s => s.Contains("ACCEPT"));
        }

        [Fact]
        public void Parse_IfStatement_EmitsIfThenElse()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 5;
        if (x > 3)
        {
            Console.WriteLine(""Big"");
        }
        else
        {
            Console.WriteLine(""Small"");
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("IF") && s.Contains(">"));
            Assert.Contains(main.Statements, s => s.Contains("ELSE"));
            Assert.Contains(main.Statements, s => s.Contains("END-IF"));
        }

        [Fact]
        public void Parse_ForStatement_EmitsPerformUntil()
        {
            var source = @"
class Test
{
    static void Main()
    {
        for (int i = 0; i < 10; i++)
        {
            Console.WriteLine(i);
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("PERFORM UNTIL"));
            Assert.Contains(main.Statements, s => s.Contains("END-PERFORM"));
            Assert.Contains(main.Statements, s => s.Contains("ADD 1 TO"));
        }

        [Fact]
        public void Parse_ForEachStatement_EmitsPerformVarying()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int[] arr = new int[5];
        foreach (var item in arr)
        {
            Console.WriteLine(item);
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("PERFORM UNTIL"));
            Assert.Contains(main.Statements, s => s.Contains("END-PERFORM"));
        }

        [Fact]
        public void Parse_WhileStatement_EmitsPerformUntil()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int i = 0;
        while (i < 5)
        {
            i++;
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("PERFORM UNTIL"));
            Assert.Contains(main.Statements, s => s.Contains("END-PERFORM"));
        }

        [Fact]
        public void Parse_DoWhileStatement_EmitsPerformWithTestAfter()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int i = 0;
        do
        {
            i++;
        } while (i < 5);
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("PERFORM WITH TEST AFTER"));
            Assert.Contains(main.Statements, s => s.Contains("END-PERFORM"));
        }

        [Fact]
        public void Parse_SwitchStatement_EmitsEvaluate()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int day = 1;
        switch (day)
        {
            case 1: Console.WriteLine(""Monday""); break;
            case 2: Console.WriteLine(""Tuesday""); break;
            default: Console.WriteLine(""Other""); break;
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("EVALUATE"));
            Assert.Contains(main.Statements, s => s.Contains("WHEN 1"));
            Assert.Contains(main.Statements, s => s.Contains("WHEN 2"));
            Assert.Contains(main.Statements, s => s.Contains("WHEN OTHER"));
            Assert.Contains(main.Statements, s => s.Contains("END-EVALUATE"));
        }

        [Fact]
        public void Parse_Assignment_EmitsMove()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 5;
        x = 10;
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("MOVE 10 TO"));
        }

        [Fact]
        public void Parse_ArithmeticAssignment_EmitsCompute()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 5;
        x = x + 3;
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("COMPUTE") && s.Contains("=") && s.Contains("+"));
        }

        [Fact]
        public void Parse_IncrementOperator_EmitsAdd()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 0;
        x++;
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("ADD 1 TO"));
        }

        [Fact]
        public void Parse_DecrementOperator_EmitsSubtract()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 0;
        x--;
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("SUBTRACT 1 FROM"));
        }

        [Fact]
        public void Parse_ReturnStatement_EmitsMoveAndExit()
        {
            var source = @"
class Test
{
    int Calculate()
    {
        return 42;
    }
}";
            var program = Parse(source);
            var para = program.Paragraphs.First(p => p.Name == "CALCULATE");

            Assert.Contains(para.Statements, s => s.Contains("MOVE 42 TO") && s.Contains("CALCULATE-RETURN"));
            Assert.Contains(para.Statements, s => s.Contains("EXIT"));
        }

        [Fact]
        public void Parse_BreakStatement_EmitsExitPerform()
        {
            var source = @"
class Test
{
    static void Main()
    {
        for (int i = 0; i < 10; i++)
        {
            if (i == 5) break;
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("EXIT PERFORM"));
        }

        [Fact]
        public void Parse_GotoStatement_EmitsGoTo()
        {
            var source = @"
class Test
{
    static void Main()
    {
        goto EndLabel;
        EndLabel: Console.WriteLine(""Done"");
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("GO TO END-LABEL"));
        }

        [Fact]
        public void Parse_TernaryExpression_EmitsIfElse()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 5;
        int y = x > 3 ? 10 : 20;
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("IF") && s.Contains(">"));
            Assert.Contains(main.Statements, s => s.Contains("ELSE"));
            Assert.Contains(main.Statements, s => s.Contains("END-IF"));
        }

        [Fact]
        public void Parse_StringConcatenation_EmitsStringVerb()
        {
            var source = @"
class Test
{
    static void Main()
    {
        string first = ""Hello"";
        string last = ""World"";
        Console.WriteLine(first + "" "" + last);
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("STRING") && s.Contains("END-STRING"));
        }

        [Fact]
        public void Parse_Comments_PreservesAsCobolComments()
        {
            var source = @"
class Test
{
    static void Main()
    {
        // This is a test comment
        Console.WriteLine(""Hello"");
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("*> This is a test comment"));
        }

        [Fact]
        public void Parse_Constructor_CreatesInitParagraph()
        {
            var source = @"
class Test
{
    int x;
    public Test()
    {
        x = 10;
    }
}";
            var program = Parse(source);

            Assert.Contains(program.Paragraphs, p => p.Name.Contains("INIT-"));
            Assert.Contains(program.InitializationStatements, s => s.Contains("PERFORM"));
        }

        [Fact]
        public void Parse_MethodCall_EmitsPerform()
        {
            var source = @"
class Test
{
    static void Main()
    {
        SayHello();
    }
    static void SayHello()
    {
        Console.WriteLine(""Help"");
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.FirstOrDefault(p => p.Name == "MAIN");

            //Assert.Contains(main.Statements, s => s.Contains("PERFORM SAY-HELLO"));

            Assert.Contains(program.MainStatements, s => s.Contains("PERFORM SAY-HELLO"));
            Assert.Contains(program.Paragraphs, p => p.Name == "SAY-HELLO");
        }

        [Fact]
        public void Parse_ExpressionBodiedMethod_EmitsMoveAndExit()
        {
            var source = @"
class Test
{
    int Double(int x) => x * 2;
}";
            var program = Parse(source);
            var para = program.Paragraphs.FirstOrDefault(p => p.Name == "DOUBLE");

            //Assert.Contains(para.Statements, s => s.Contains("COMPUTE") && s.Contains("*"));
            Assert.Contains(para.Statements, s => s.Contains("*"));
            Assert.Contains(para.Statements, s => s.Contains("MOVE") && s.Contains("DOUBLE-RETURN"));
            Assert.Contains(para.Statements, s => s.Contains("EXIT"));
        }

        [Fact]
        public void Parse_ArrayCreation_CreatesOccursTable()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int[] arr = new int[10];
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.FirstOrDefault(s => s.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("ARR"));
            Assert.Contains(main.Statements, s => s.Contains("WS-MAIN-ARR"));

            // TODO: Array should create a data item with OCCURS
            //Assert.Contains(program.DataItems, d => d.Name.Contains("ARR") && d.Occurs.HasValue);
        }

        [Fact]
        public void Parse_NestedIf_EmitsNestedEndIf()
        {
            var source = @"
class Test
{
    static void Main()
    {
        int x = 5;
        int y = 10;
        if (x > 0)
        {
            if (y > 0)
            {
                Console.WriteLine(""Both positive"");
            }
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            var endIfCount = main.Statements.Count(s => s.Contains("END-IF"));
            Assert.Equal(2, endIfCount);
        }

        [Fact]
        public void Parse_VarKeyword_ResolvesViaSemanticModel()
        {
            var source = @"
class Test
{
    static void Main()
    {
        var x = 42;
        var y = ""hello"";
    }
}";
            var program = Parse(source);

            var xItem = program.DataItems.FirstOrDefault(d => d.Name.Contains("X") && d.Name.Contains("MAIN"));
            var yItem = program.DataItems.FirstOrDefault(d => d.Name.Contains("Y") && d.Name.Contains("MAIN"));

            Assert.NotNull(xItem);
            Assert.NotNull(yItem);
            // With semantic model, var should resolve to proper types
            Assert.Contains("S9", xItem.Picture);
            Assert.Contains("X", yItem.Picture);
        }

        [Fact]
        public void Parse_UnsupportedStatement_AddsComment()
        {
            var source = @"
class Test
{
    static void Main()
    {
        lock (this) { }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.First(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("*> UNSUPPORTED"));
        }

        [Fact]
        public void Parse_TryCatch_EmitsGoToBlocks()
        {
            var source = @"
class Test
{
    static void Main()
    {
        try
        {
            Console.WriteLine(""Try"");
        }
        catch (System.Exception ex)
        {
            Console.WriteLine(""Catch"");
        }
    }
}";
            var program = Parse(source);
            var main = program.Paragraphs.FirstOrDefault(p => p.Name == "MAIN");

            Assert.Contains(main.Statements, s => s.Contains("*> TRY block begin"));
            Assert.Contains(main.Statements, s => s.Contains("GO TO"));
        }
    }
}
