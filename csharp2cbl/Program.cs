using System;
using System.Collections.Generic;
using System.Text;

namespace csharp2cbl
{
    internal class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  C# to COBOL Transpiler v1.0.0");
            Console.WriteLine("  Target: zCOBOL / GnuCOBOL / IBM");
            Console.WriteLine("========================================");
            Console.WriteLine();

            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            string inputPath = null;
            string outputPath = null;
            string programId = null;
            string dialect = "zcobol";

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input":
                    case "-i":
                        inputPath = i + 1 < args.Length ? args[++i] : null;
                        break;
                    case "--output":
                    case "-o":
                        outputPath = i + 1 < args.Length ? args[++i] : null;
                        break;
                    case "--program-id":
                    case "-p":
                        programId = i + 1 < args.Length ? args[++i] : null;
                        break;
                    case "--dialect":
                    case "-d":
                        dialect = i + 1 < args.Length ? args[++i] : "zcobol";
                        break;
                }
            }

            if (string.IsNullOrEmpty(inputPath))
            {
                Console.Error.WriteLine("Error: --input is required.");
                PrintUsage();
                return 1;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
                return 1;
            }

            try
            {
                string csharpSource = File.ReadAllText(inputPath);
                if (string.IsNullOrWhiteSpace(csharpSource))
                {
                    Console.Error.WriteLine("Error: Input file is empty.");
                    return 1;
                }

                var translator = new CblTranslator(dialect);
                string cobolCode = translator.Translate(csharpSource, programId);

                outputPath ??= Path.ChangeExtension(inputPath, ".cbl");
                File.WriteAllText(outputPath, cobolCode);

                Console.WriteLine($"Success: Generated {outputPath}");
                Console.WriteLine($"  Lines: {cobolCode.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length}");
                Console.WriteLine($"  Dialect: {dialect}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Transpilation failed: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: CSharp2Cobol --input <file.cs> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -i, --input <path>       Required. C# source file to transpile");
            Console.WriteLine("  -o, --output <path>      Output .cbl file (default: <input>.cbl)");
            Console.WriteLine("  -p, --program-id <name>  COBOL PROGRAM-ID (default: sanitized class name)");
            Console.WriteLine("  -d, --dialect <name>     Target: zcobol | ibm | gnucobol (default: zcobol)");
            Console.WriteLine("  -h, --help               Show this help");
        }
    }
}

