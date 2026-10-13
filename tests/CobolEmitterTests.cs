namespace tests
{
    public class CobolEmitterTests
    {
        private CobolEmitter CreateEmitter(string dialect = "zcobol")
        {
            return new CobolEmitter(dialect);
        }

        private CobolProgram CreateMinimalProgram()
        {
            return new CobolProgram
            {
                ProgramId = "TESTPGM",
                DataItems = new System.Collections.Generic.List<CobolDataItem>(),
                Paragraphs = new System.Collections.Generic.List<CobolParagraph>(),
                MainStatements = new System.Collections.Generic.List<string>(),
                InitializationStatements = new System.Collections.Generic.List<string>(),
                Comments = new System.Collections.Generic.List<string>()
            };
        }

        [Fact]
        public void Emit_MinimalProgram_ContainsIdentificationDivision()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();

            var result = emitter.Emit(program);

            Assert.Contains("IDENTIFICATION DIVISION.", result);
            Assert.Contains("PROGRAM-ID. TESTPGM.", result);
        }

        [Fact]
        public void Emit_MinimalProgram_ContainsEnvironmentDivision()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();

            var result = emitter.Emit(program);

            Assert.Contains("ENVIRONMENT DIVISION.", result);
            Assert.Contains("SOURCE-COMPUTER. IBM-370.", result);
            Assert.Contains("OBJECT-COMPUTER. IBM-370.", result);
        }

        [Fact]
        public void Emit_MinimalProgram_ContainsDataDivision()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();

            var result = emitter.Emit(program);

            Assert.Contains("DATA DIVISION.", result);
            Assert.Contains("WORKING-STORAGE SECTION.", result);
        }

        [Fact]
        public void Emit_MinimalProgram_ContainsProcedureDivision()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();

            var result = emitter.Emit(program);

            Assert.Contains("PROCEDURE DIVISION.", result);
        }

        [Fact]
        public void Emit_ProgramWithDataItem_EmitsDataItem()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.DataItems.Add(new CobolDataItem
            {
                Level = 1,
                Name = "WS-COUNTER",
                Picture = "S9(9)",
                Value = "0"
            });

            var result = emitter.Emit(program);

            Assert.Contains("01  WS-COUNTER PIC S9(9) VALUE 0.", result);
        }

        [Fact]
        public void Emit_ProgramWithNestedDataItem_EmitsHierarchy()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.DataItems.Add(new CobolDataItem
            {
                Level = 1,
                Name = "WS-RECORD",
                Children = new System.Collections.Generic.List<CobolDataItem>
                {
                    new CobolDataItem { Level = 5, Name = "WS-FIELD1", Picture = "X(10)" },
                    new CobolDataItem { Level = 5, Name = "WS-FIELD2", Picture = "9(5)" }
                }
            });

            var result = emitter.Emit(program);

            Assert.Contains("01  WS-RECORD.", result);
            Assert.Contains("05  WS-FIELD1 PIC X(10).", result);
            Assert.Contains("05  WS-FIELD2 PIC 9(5).", result);
        }

        [Fact]
        public void Emit_ProgramWithMainStatements_EmitsMainParagraph()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.MainStatements.Add("DISPLAY 'HELLO'");
            program.MainStatements.Add("STOP RUN");

            var result = emitter.Emit(program);

            Assert.Contains("MAIN.", result);
            Assert.Contains("DISPLAY", result);
            Assert.Contains("STOP RUN", result);
        }

        [Fact]
        public void Emit_ProgramWithParagraphs_EmitsParagraphs()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.Paragraphs.Add(new CobolParagraph
            {
                Name = "CALCULATE",
                Statements = new System.Collections.Generic.List<string> { "ADD 1 TO WS-COUNTER", "EXIT" }
            });

            var result = emitter.Emit(program);

            Assert.Contains("CALCULATE.", result);
            Assert.Contains("ADD 1 TO WS-COUNTER", result);
            Assert.Contains("EXIT", result);
        }

        [Fact]
        public void Emit_ProgramWithComments_EmitsComments()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.Comments.Add("Generated by CSharp2Cobol");

            var result = emitter.Emit(program);

            Assert.Contains("*> Generated by CSharp2Cobol", result);
        }

        [Fact]
        public void Emit_ProgramWithFileItems_EmitsFileControl()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.FileItems.Add(new CobolFileItem
            {
                FileName = "FD-INPUT",
                ExternalName = "INPUT.TXT",
                Organization = "LINE SEQUENTIAL",
                AccessMode = "SEQUENTIAL"
            });

            var result = emitter.Emit(program);

            Assert.Contains("INPUT-OUTPUT SECTION.", result);
            Assert.Contains("FILE-CONTROL.", result);
            Assert.Contains("SELECT FD-INPUT ASSIGN TO 'INPUT.TXT'", result);
        }

        [Fact]
        public void Emit_ProgramWithInitialization_EmitsInitParagraph()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.InitializationStatements.Add("MOVE 0 TO WS-COUNTER");

            var result = emitter.Emit(program);

            Assert.Contains("INIT-PROGRAM.", result);
            Assert.Contains("MOVE 0 TO WS-COUNTER", result);
        }

        [Fact]
        public void Emit_ProgramEndsWithEndProgram()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();

            var result = emitter.Emit(program);

            Assert.Contains("END PROGRAM TESTPGM.", result);
        }

        [Fact]
        public void Emit_LongStatement_WrapsCorrectly()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.MainStatements.Add(
                "MOVE 'THIS IS A VERY LONG STRING THAT SHOULD EXCEED THE SEVENTY TWO COLUMN LIMIT AND NEED TO BE WRAPPED ACROSS MULTIPLE LINES' TO WS-TEMP");

            var result = emitter.Emit(program);
            var lines = result.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);

            // Check that at least one continuation line exists (hyphen in column 7 area)
            bool hasContinuation = lines.Any(l => l.Length > 6 && l[6] == '-');
            Assert.True(hasContinuation, "Long statements should be wrapped with continuation lines");
        }

        [Fact]
        public void Emit_DataItemWithOccurs_EmitsOccurs()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.DataItems.Add(new CobolDataItem
            {
                Level = 1,
                Name = "WS-TABLE",
                Picture = "X(10)",
                Occurs = 5
            });

            var result = emitter.Emit(program);

            Assert.Contains("OCCURS 5 TIMES", result);
        }

        [Fact]
        public void Emit_DataItemWithConditionName_Emits88Level()
        {
            var emitter = CreateEmitter();
            var program = CreateMinimalProgram();
            program.DataItems.Add(new CobolDataItem
            {
                Level = 1,
                Name = "WS-STATUS",
                Picture = "9",
                ConditionNames = new System.Collections.Generic.List<CobolConditionName>
                {
                    new CobolConditionName { Name = "STATUS-OK", Value = "0" }
                }
            });

            var result = emitter.Emit(program);

            Assert.Contains("88  STATUS-OK VALUE 0.", result);
        }

        [Fact]
        public void Emit_DifferentDialects_ProducesSameStructure()
        {
            var program = CreateMinimalProgram();
            program.MainStatements.Add("STOP RUN");

            var zcobolResult = CreateEmitter("zcobol").Emit(program);
            var ibmResult = CreateEmitter("ibm").Emit(program);
            var gnuResult = CreateEmitter("gnucobol").Emit(program);

            // All should have the basic structure
            Assert.Contains("PROGRAM-ID. TESTPGM.", zcobolResult);
            Assert.Contains("PROGRAM-ID. TESTPGM.", ibmResult);
            Assert.Contains("PROGRAM-ID. TESTPGM.", gnuResult);
        }
    }
}
