using System;
using System.Collections.Generic;
using System.Text;

namespace tests
{
    public class CobolProgramTests
    {
        [Fact]
        public void CobolDataItem_ToString_WithAllProperties_ReturnsFormattedString()
        {
            var item = new CobolDataItem
            {
                Level = 5,
                Name = "WS-NAME",
                Picture = "X(20)",
                Usage = "COMP",
                Value = "'TEST'"
            };

            var result = item.ToString();

            Assert.Contains("5 WS-NAME", result);
            Assert.Contains("PIC X(20)", result);
            Assert.Contains("USAGE COMP", result);
            Assert.Contains("VALUE 'TEST'", result);
        }

        [Fact]
        public void CobolDataItem_ToString_WithoutOptionalProperties_ReturnsMinimalString()
        {
            var item = new CobolDataItem
            {
                Level = 1,
                Name = "WS-ROOT"
            };

            var result = item.ToString();

            Assert.Equal("01 WS-ROOT", result);
        }

        [Fact]
        public void CobolDataItem_WithChildren_EmitsHierarchy()
        {
            var parent = new CobolDataItem
            {
                Level = 1,
                Name = "WS-RECORD",
                Children = new List<CobolDataItem>
                {
                    new CobolDataItem { Level = 5, Name = "WS-FIELD1", Picture = "X(10)" },
                    new CobolDataItem { Level = 5, Name = "WS-FIELD2", Picture = "9(5)" }
                }
            };

            Assert.Equal(2, parent.Children.Count);
            Assert.Equal("WS-FIELD1", parent.Children[0].Name);
            Assert.Equal("WS-FIELD2", parent.Children[1].Name);
        }

        [Fact]
        public void CobolDataItem_WithConditionNames_HasConditionList()
        {
            var item = new CobolDataItem
            {
                Level = 1,
                Name = "WS-STATUS",
                Picture = "9",
                ConditionNames = new List<CobolConditionName>
                {
                    new CobolConditionName { Name = "STATUS-OK", Value = "0" },
                    new CobolConditionName { Name = "STATUS-ERR", Value = "1" }
                }
            };

            Assert.Equal(2, item.ConditionNames.Count);
            Assert.Equal("STATUS-OK", item.ConditionNames[0].Name);
        }

        [Fact]
        public void CobolDataItem_WithOccurs_HasOccursValue()
        {
            var item = new CobolDataItem
            {
                Level = 1,
                Name = "WS-TABLE",
                Picture = "X(10)",
                Occurs = 10
            };

            var result = item.ToString();
            Assert.Contains("OCCURS 10 TIMES", result);
        }

        [Fact]
        public void CobolDataItem_WithOccursDependingOn_HasDependingOn()
        {
            var item = new CobolDataItem
            {
                Level = 1,
                Name = "WS-VAR-TABLE",
                Picture = "X(10)",
                Occurs = 1,
                DependsOn = "WS-COUNT"
            };

            var result = item.ToString();
            Assert.Contains("OCCURS 1 TO WS-COUNT TIMES DEPENDING ON WS-COUNT", result);
        }

        [Fact]
        public void CobolDataItem_WithRedefines_HasRedefines()
        {
            var item = new CobolDataItem
            {
                Level = 1,
                Name = "WS-ALT",
                Picture = "X(20)",
                Redefines = "WS-ORIG"
            };

            var result = item.ToString();
            Assert.Contains("REDEFINES WS-ORIG", result);
        }

        [Fact]
        public void CobolParagraph_ToString_ContainsNameAndStatements()
        {
            var para = new CobolParagraph
            {
                Name = "MAIN",
                Statements = new List<string> { "DISPLAY 'HELLO'", "STOP RUN" }
            };

            var result = para.ToString();

            Assert.Contains("MAIN.", result);
            Assert.Contains("DISPLAY 'HELLO'", result);
            Assert.Contains("STOP RUN", result);
        }

        [Fact]
        public void CobolParagraph_WithComments_IncludesComments()
        {
            var para = new CobolParagraph
            {
                Name = "TEST",
                Comments = new List<string> { "This is a test" },
                Statements = new List<string>()
            };

            var result = para.ToString();
            Assert.Contains("*> This is a test", result);
        }

        [Fact]
        public void CobolProgram_Defaults_AreSet()
        {
            var program = new CobolProgram
            {
                ProgramId = "TESTPGM"
            };

            Assert.Equal("TESTPGM", program.ProgramId);
            Assert.NotNull(program.DataItems);
            Assert.NotNull(program.Paragraphs);
            Assert.NotNull(program.MainStatements);
            Assert.NotNull(program.FileItems);
            Assert.NotNull(program.InitializationStatements);
            Assert.NotNull(program.Comments);
            Assert.Equal("csharp2cobol transpiler".ToLowerInvariant(), program.Author.ToLowerInvariant());
            Assert.NotNull(program.DateCompiled);
        }

        [Fact]
        public void CobolFileItem_Defaults_AreSet()
        {
            var file = new CobolFileItem
            {
                FileName = "FD-TEST"
            };

            Assert.Equal("LINE SEQUENTIAL", file.Organization);
            Assert.Equal("SEQUENTIAL", file.AccessMode);
            Assert.NotNull(file.RecordFields);
        }
    }
}
