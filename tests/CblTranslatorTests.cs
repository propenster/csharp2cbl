using System;
using System.Collections.Generic;
using System.Text;

namespace tests
{
    public class CblTranslatorTests
    {
        private CblTranslator CreateTranslator(string dialect = "zcobol")
        {
            return new CblTranslator(dialect);
        }

        [Fact]
        public void Translate_HelloWorld_ProducesValidCobol()
        {
            var translator = CreateTranslator();
            var csharp = @"using System;
class Program
{
    static void Main()
    {
        Console.WriteLine(""Hello, World!"");
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("IDENTIFICATION DIVISION.", result);
            Assert.Contains("PROGRAM-ID. PROGRAM.", result);
            Assert.Contains("PROCEDURE DIVISION.", result);
            Assert.Contains("MAIN.", result);
            Assert.Contains("STOP RUN", result);
            Assert.Contains("END PROGRAM PROGRAM.", result);
        }

        [Fact]
        public void Translate_TopLevelStatements_ProducesValidCobol()
        {
            var translator = CreateTranslator();
            var csharp = @"using System;
Console.WriteLine(""Hello from top level"");";
            var result = translator.Translate(csharp);

            Assert.Contains("PROGRAM-ID. CSHARP2CBL.", result);
            Assert.Contains("STOP RUN", result);
        }

        [Fact]
        public void Translate_CustomProgramId_UsesProvidedId()
        {
            var translator = CreateTranslator();
            var csharp = @"class Test { }";
            var result = translator.Translate(csharp, "MYPGM01");

            Assert.Contains("PROGRAM-ID. MYPGM01.", result);
        }

        [Fact]
        public void Translate_WithVariables_ProducesWorkingStorage()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        int counter = 0;
        string name = ""John"";
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("WORKING-STORAGE SECTION.", result);
            Assert.Contains("COUNTER", result);
            Assert.Contains("NAME", result);
        }

        [Fact]
        public void Translate_WithIfStatement_ProducesConditionalLogic()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        int x = 5;
        if (x > 3)
        {
            Console.WriteLine(""Greater"");
        }
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("IF", result);
            Assert.Contains("THEN", result);
            Assert.Contains("END-IF", result);
        }

        [Fact]
        public void Translate_WithForLoop_ProducesPerformLoop()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        for (int i = 0; i < 5; i++)
        {
            Console.WriteLine(i);
        }
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("PERFORM UNTIL", result);
            Assert.Contains("END-PERFORM", result);
            Assert.Contains("ADD 1 TO", result);
        }

        [Fact]
        public void Translate_WithMethod_ProducesParagraphs()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        SayHello();
    }
    static void SayHello()
    {
        Console.WriteLine(""Hello"");
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("SAY-HELLO.", result);
            Assert.Contains("PERFORM SAY-HELLO", result);
            Assert.Contains("EXIT", result);
        }

        [Fact]
        public void Translate_WithArithmetic_ProducesCompute()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        int a = 5;
        int b = 3;
        int c = a + b;
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("COMPUTE", result);
            Assert.Contains("+", result);
        }

        [Fact]
        public void Translate_WithStringConcat_ProducesStringVerb()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        string first = ""John"";
        string last = ""Doe"";
        string full = first + "" "" + last;
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("STRING", result);
            Assert.Contains("END-STRING", result);
            Assert.Contains("DELIMITED BY SIZE", result);
        }

        [Fact]
        public void Translate_WithSwitch_ProducesEvaluate()
        {
            var translator = CreateTranslator();
            var csharp = @"
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
            var result = translator.Translate(csharp);

            Assert.Contains("EVALUATE", result);
            Assert.Contains("WHEN 1", result);
            Assert.Contains("WHEN OTHER", result);
            Assert.Contains("END-EVALUATE", result);
        }

        [Fact]
        public void Translate_WithComments_PreservesComments()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        // This is an important comment
        Console.WriteLine(""Hello"");
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("*> This is an important comment", result);
        }

        [Fact]
        public void Translate_InvalidCSharp_ThrowsException()
        {
            var translator = CreateTranslator();
            var csharp = @"this is not valid C#";

            var ex = Assert.Throws<InvalidOperationException>(() => translator.Translate(csharp));
            Assert.Contains("compilation errors", ex.Message);
        }

        [Fact]
        public void Translate_WithWhileLoop_ProducesPerformUntil()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        int i = 0;
        while (i < 10)
        {
            i++;
        }
    }
}";
            var result = translator.Translate(csharp);
            Console.WriteLine($"{result}");

            Assert.Contains("PERFORM UNTIL", result);
            Assert.Contains("END-PERFORM", result);
        }

        [Fact]
        public void Translate_WithDoWhile_ProducesPerformWithTestAfter()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static void Main()
    {
        int i = 0;
        do
        {
            i++;
        } while (i < 10);
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("PERFORM WITH TEST AFTER", result);
            Assert.Contains("END-PERFORM", result);
        }

        [Fact]
        public void Translate_WithClassFields_ProducesWorkingStorage()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    int globalCounter = 0;
    string globalName = ""Test"";
    static void Main()
    {
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("WORKING-STORAGE SECTION.", result);
            Assert.Contains("WS-GLOBAL-COUNTER", result);
            Assert.Contains("WS-GLOBAL-NAME", result);
        }

        [Fact]
        public void Translate_WithReturn_ProducesMoveToReturnVar()
        {
            var translator = CreateTranslator();
            var csharp = @"
class Test
{
    static int Add(int a, int b)
    {
        return a + b;
    }
}";
            var result = translator.Translate(csharp);

            Assert.Contains("ADD.", result);
            Assert.Contains("ADD-RETURN", result);
            Assert.Contains("EXIT", result);
        }

        [Fact]
        public void Translate_WithDifferentDialects_ProducesSameStructure()
        {
            var csharp = @"class Test { static void Main() { Console.WriteLine(""Hi""); } }";

            var zcobol = new CblTranslator("zcobol").Translate(csharp);
            var ibm = new CblTranslator("ibm").Translate(csharp);
            var gnu = new CblTranslator("gnucobol").Translate(csharp);

            Assert.Contains("PROGRAM-ID. TEST.", zcobol);
            Assert.Contains("PROGRAM-ID. TEST.", ibm);
            Assert.Contains("PROGRAM-ID. TEST.", gnu);
        }

        [Fact]
        public void Translate_ComplexProgram_ProducesCompilableStructure()
        {
            var translator = CreateTranslator();
            var csharp = @"
using System;

namespace Demo
{
    class Calculator
    {
        int result = 0;

        static void Main(string[] args)
        {
            Calculator calc = new Calculator();
            calc.Add(5, 3);
            Console.WriteLine(calc.result);
        }

        void Add(int a, int b)
        {
            result = a + b;
        }
    }
}";
            var result = translator.Translate(csharp);

            // Should have all divisions
            Assert.Contains("IDENTIFICATION DIVISION.", result);
            Assert.Contains("ENVIRONMENT DIVISION.", result);
            Assert.Contains("DATA DIVISION.", result);
            Assert.Contains("PROCEDURE DIVISION.", result);

            // Should have paragraphs
            Assert.Contains("MAIN.", result);
            Assert.Contains("ADD.", result);

            // Should have STOP RUN in MAIN
            Assert.Contains("STOP RUN", result);

            // Should end properly
            Assert.Contains("END PROGRAM CALCULATOR.", result);
        }
    }
}
