using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace tests
{
    public class MapperTests
    {
        [Theory]
        [InlineData("int", "PIC S9(9)")]
        [InlineData("long", "PIC S9(18)")]
        [InlineData("short", "PIC S9(4)")]
        [InlineData("string", "PIC X(255)")]
        [InlineData("bool", "PIC 9")]
        [InlineData("decimal", "PIC S9(13)V9(2)")]
        [InlineData("float", "COMP-1")]
        [InlineData("double", "COMP-2")]
        [InlineData("char", "PIC X(1)")]
        [InlineData("byte", "PIC 9(3)")]
        [InlineData("sbyte", "PIC S9(3)")]
        [InlineData("uint", "PIC 9(9)")]
        [InlineData("ulong", "PIC 9(18)")]
        [InlineData("ushort", "PIC 9(4)")]
        [InlineData("object", "PIC X(255)")]
        [InlineData("var", "PIC X(255)")]
        [InlineData("unknown", "PIC X(255)")]
        public void MapType_KnownTypes_ReturnsCorrectPic(string csharpType, string expectedPic)
        {
            var tree = CSharpSyntaxTree.ParseText($"class T {{ {csharpType} x; }}");
            var root = tree.GetCompilationUnitRoot();
            var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>().First();
            var actual = Mapper.MapType(field.Declaration.Type);
            Assert.Equal(expectedPic, actual);
        }

        [Theory]
        [InlineData("==", "=")]
        [InlineData("!=", "<>")]
        [InlineData(">", ">")]
        [InlineData("<", "<")]
        [InlineData(">=", ">=")]
        [InlineData("<=", "<=")]
        [InlineData("+", "+")]
        [InlineData("-", "-")]
        [InlineData("*", "*")]
        [InlineData("/", "/")]
        [InlineData("%", "FUNCTION MOD")]
        [InlineData("||", "OR")]
        [InlineData("&&", "AND")]
        [InlineData("!", "NOT")]
        public void MapOperator_KnownOperators_ReturnsCorrectCobol(string csharpOp, string expectedCobol)
        {
            var actual = Mapper.MapOperator(csharpOp);
            Assert.Equal(expectedCobol, actual);
        }

        [Theory]
        [InlineData("HelloWorld", "HELLOWOR")]
        [InlineData("MyApp123", "MYAPP123")]
        [InlineData("123App", "P123APP")]
        [InlineData("", "CSHARP2CBL")]
        [InlineData("A-Very-Long-Program-Name-That-Exceeds", "AVERYLONGPROGRAMNAMETHATEXCEEDS")]
        public void SanitizeProgramId_VariousInputs_ReturnsValidId(string input, string expected)
        {
            var actual = Mapper.SanitizeProgramId(input);
            Assert.StartsWith(expected.Substring(0, Math.Min(expected.Length, 8)), actual);
            Assert.True(actual.Length <= 30);
            Assert.True(char.IsLetter(actual[0]));
        }

        [Theory]
        [InlineData("Main", "MAIN")]
        [InlineData("CalculateTotal", "CALCULATE-TOTAL")]
        [InlineData("123Method", "P-123METHOD")]
        [InlineData("", "P-MAIN")]
        public void SanitizeParagraphName_VariousInputs_ReturnsValidName(string input, string expected)
        {
            var actual = Mapper.SanitizeParagraphName(input);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("myVariable", "WS-MY-VARIABLE")]
        [InlineData("123Var", "WS-123VAR")]
        [InlineData("", "WS-ITEM")]
        [InlineData("already-ws-prefixed", "WS-ALREADY-WS-PREFIXED")]
        public void SanitizeDataName_VariousInputs_ReturnsValidName(string input, string expected)
        {
            var actual = Mapper.SanitizeDataName(input);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void SanitizeDataName_EnsuresWsPrefix()
        {
            var actual = Mapper.SanitizeDataName("counter");
            Assert.StartsWith("WS-", actual);
        }

        [Fact]
        public void SanitizeFileName_EnsuresFdPrefix()
        {
            var actual = Mapper.SanitizeFileName("myFile");
            Assert.StartsWith("FD-", actual);
        }
    }
}
