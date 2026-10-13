using csharp2cbl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;


/// <summary>
/// Result of evaluating a C# expression into COBOL.
/// Contains the COBOL operand/reference string and any setup statements that must execute before use.
/// </summary>
public class ExpressionResult
{
    public string Code { get; set; }
    public List<string> SetupStatements { get; set; } = new List<string>();
    public bool IsLiteral => !string.IsNullOrEmpty(Code) && (Code.StartsWith("'") || char.IsDigit(Code[0]) || Code == "1" || Code == "0");
}

public class Parser
{
    private CobolProgram _program;
    private string _currentMethod;
    private int _tempCounter;
    private SemanticModel _semanticModel;
    private readonly List<string> _pendingComments = new();

    public Parser(SemanticModel semanticModel = null)
    {
        _semanticModel = semanticModel;
    }

    public CobolProgram Parse(CompilationUnitSyntax root, string programId)
    {
        _program = new CobolProgram();
        _program.ProgramId = programId ?? "CSHARP2CBL";
        _program.DataItems = new List<CobolDataItem>();
        _program.FileItems = new List<CobolFileItem>();
        _program.Paragraphs = new List<CobolParagraph>();
        _program.MainStatements = new List<string>();
        _tempCounter = 0;

        // Extract comments from leading trivia of the compilation unit
        ExtractComments(root.GetLeadingTrivia());

        // Handle top-level statements (C# 9+)
        var topLevelStatements = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (topLevelStatements.Any())
        {
            ProcessTopLevelStatements(topLevelStatements);
        }

        // Extract namespace and class info
        var namespaceDecl = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        var fileScopedNs = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        if (classDecl != null && string.IsNullOrEmpty(programId))
        {
            _program.ProgramId = Mapper.SanitizeProgramId(classDecl.Identifier.Text);
        }

        // Process class-level members (fields, properties, methods)
        if (classDecl != null)
        {
            ProcessClassMembers(classDecl);
        }
        else if (!topLevelStatements.Any())
        {
            // No class and no top-level statements — try to find methods at root level (unlikely but possible)
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                ProcessMethod(method);
            }
        }

        // If no explicit paragraphs were created but we have main statements, ensure MAIN exists
        if (_program.Paragraphs.Count == 0 && _program.MainStatements.Count > 0)
        {
            _program.Paragraphs.Add(new CobolParagraph
            {
                Name = "MAIN",
                Statements = new List<string>(_program.MainStatements)
            });
        }

        return _program;
    }

    private void ProcessTopLevelStatements(List<GlobalStatementSyntax> topLevelStatements)
    {
        _currentMethod = "MAIN";
        var paragraph = new CobolParagraph
        {
            Name = "MAIN",
            Statements = new List<string>()
        };

        foreach (var globalStmt in topLevelStatements)
        {
            ExtractComments(globalStmt.GetLeadingTrivia());
            if (_pendingComments.Any())
            {
                paragraph.Comments.AddRange(_pendingComments);
                _pendingComments.Clear();
            }

            var stmts = ProcessStatement(globalStmt.Statement);
            paragraph.Statements.AddRange(stmts);
        }

        paragraph.Statements.Add("STOP RUN");
        _program.MainStatements = paragraph.Statements;
        _program.Paragraphs.Add(paragraph);
    }

    private void ProcessClassMembers(ClassDeclarationSyntax classDecl)
    {
        foreach (var member in classDecl.Members)
        {
            ExtractComments(member.GetLeadingTrivia());

            switch (member)
            {
                case FieldDeclarationSyntax field:
                    ProcessField(field);
                    break;
                case PropertyDeclarationSyntax prop:
                    ProcessProperty(prop);
                    break;
                case MethodDeclarationSyntax method:
                    ProcessMethod(method);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    ProcessConstructor(ctor);
                    break;
            }
        }
    }

    private void ProcessField(FieldDeclarationSyntax field)
    {
        string picClause = GetPicClause(field.Declaration.Type);
        bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
        bool isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword);

        foreach (var variable in field.Declaration.Variables)
        {
            string varName = Mapper.SanitizeDataName(variable.Identifier.Text);
            var dataItem = new CobolDataItem
            {
                Level = 01,
                Name = varName,
                Picture = picClause
            };

            if (variable.Initializer != null)
            {
                var result = EvaluateExpression(variable.Initializer.Value);
                if (result.SetupStatements.Any())
                {
                    _program.InitializationStatements.AddRange(result.SetupStatements);
                }
                dataItem.Value = result.Code;
            }
            else if (isConst)
            {
                // Try to get constant value from semantic model
                if (_semanticModel != null && variable.Initializer != null)
                {
                    var constValue = _semanticModel.GetConstantValue(variable.Initializer.Value);
                    if (constValue.HasValue && constValue.Value != null)
                    {
                        dataItem.Value = FormatConstantValue(constValue.Value);
                    }
                }
            }

            if (_pendingComments.Any())
            {
                dataItem.Comments.AddRange(_pendingComments);
                _pendingComments.Clear();
            }

            _program.DataItems.Add(dataItem);
        }
    }

    private void ProcessProperty(PropertyDeclarationSyntax prop)
    {
        // Auto-properties become WORKING-STORAGE items
        // For v1.0.0, we treat properties as fields (backing field generated implicitly)
        string varName = Mapper.SanitizeDataName(prop.Identifier.Text);
        string picClause = GetPicClause(prop.Type);

        var dataItem = new CobolDataItem
        {
            Level = 01,
            Name = varName,
            Picture = picClause
        };

        if (prop.Initializer != null)
        {
            var result = EvaluateExpression(prop.Initializer.Value);
            if (result.SetupStatements.Any())
            {
                _program.InitializationStatements.AddRange(result.SetupStatements);
            }
            dataItem.Value = result.Code;
        }

        _program.DataItems.Add(dataItem);
    }

    private void ProcessConstructor(ConstructorDeclarationSyntax ctor)
    {
        // Constructors map to an initialization paragraph
        string ctorName = Mapper.SanitizeParagraphName($"INIT-{ctor.Identifier.Text}");
        var paragraph = new CobolParagraph
        {
            Name = ctorName,
            Statements = new List<string>()
        };

        if (ctor.Body != null)
        {
            foreach (var statement in ctor.Body.Statements)
            {
                paragraph.Statements.AddRange(ProcessStatement(statement));
            }
        }
        else if (ctor.ExpressionBody != null)
        {
            // Expression-bodied constructor
            var result = EvaluateExpression(ctor.ExpressionBody.Expression);
            if (result.SetupStatements.Any())
                paragraph.Statements.AddRange(result.SetupStatements);
        }

        _program.Paragraphs.Add(paragraph);
        _program.InitializationStatements.Add($"PERFORM {ctorName}");
    }

    private void ProcessMethod(MethodDeclarationSyntax method)
    {
        string methodName = Mapper.SanitizeParagraphName(method.Identifier.Text);
        _currentMethod = methodName;

        var paragraph = new CobolParagraph
        {
            Name = methodName,
            Statements = new List<string>()
        };

        // Process parameters as data items (method-scoped working storage)
        foreach (var param in method.ParameterList.Parameters)
        {
            string dataName = Mapper.SanitizeDataName($"{methodName}-{param.Identifier.Text}");
            string picClause = GetPicClause(param.Type);
            _program.DataItems.Add(new CobolDataItem
            {
                Level = 01,
                Name = dataName,
                Picture = picClause
            });
        }

        // Handle return type
        if (method.ReturnType.ToString() != "void")
        {
            string returnName = Mapper.SanitizeDataName($"{methodName}-RETURN");
            string returnPic = GetPicClause(method.ReturnType);
            _program.DataItems.Add(new CobolDataItem
            {
                Level = 01,
                Name = returnName,
                Picture = returnPic
            });
        }

        // Process method body
        if (method.Body != null)
        {
            foreach (var statement in method.Body.Statements)
            {
                var stmts = ProcessStatement(statement);
                paragraph.Statements.AddRange(stmts);
            }
        }
        else if (method.ExpressionBody != null)
        {
            // Expression-bodied method: result = expression; EXIT
            var result = EvaluateExpression(method.ExpressionBody.Expression);
            if (result.SetupStatements.Any())
                paragraph.Statements.AddRange(result.SetupStatements);

            string returnName = Mapper.SanitizeDataName($"{methodName}-RETURN");
            paragraph.Statements.Add($"MOVE {result.Code} TO {returnName}");
        }

        // Add termination
        if (methodName == "MAIN" || method.Identifier.Text == "Main")
        {
            paragraph.Statements.Add("STOP RUN");
            _program.MainStatements = paragraph.Statements;
        }
        else
        {
            paragraph.Statements.Add("EXIT");
            _program.Paragraphs.Add(paragraph);
        }
    }

    private List<string> ProcessStatement(StatementSyntax statement)
    {
        var result = new List<string>();
        ExtractComments(statement.GetLeadingTrivia());

        if (_pendingComments.Any())
        {
            foreach (var comment in _pendingComments)
                result.Add($"*> {comment}");
            _pendingComments.Clear();
        }

        switch (statement)
        {
            case LocalDeclarationStatementSyntax localDecl:
                result.AddRange(ProcessLocalDeclaration(localDecl));
                break;

            case ExpressionStatementSyntax exprStmt:
                result.AddRange(ProcessExpressionStatement(exprStmt));
                break;

            case IfStatementSyntax ifStmt:
                result.AddRange(ProcessIfStatement(ifStmt));
                break;

            case SwitchStatementSyntax switchStmt:
                result.AddRange(ProcessSwitchStatement(switchStmt));
                break;

            case ForStatementSyntax forStmt:
                result.AddRange(ProcessForStatement(forStmt));
                break;

            case ForEachStatementSyntax foreachStmt:
                result.AddRange(ProcessForEachStatement(foreachStmt));
                break;

            case WhileStatementSyntax whileStmt:
                result.AddRange(ProcessWhileStatement(whileStmt));
                break;

            case DoStatementSyntax doStmt:
                result.AddRange(ProcessDoStatement(doStmt));
                break;

            case ReturnStatementSyntax returnStmt:
                result.AddRange(ProcessReturnStatement(returnStmt));
                break;

            case BreakStatementSyntax:
                result.Add("EXIT PERFORM");
                break;

            case ContinueStatementSyntax:
                result.Add("*> CONTINUE — next iteration");
                break;

            case TryStatementSyntax tryStmt:
                result.AddRange(ProcessTryStatement(tryStmt));
                break;

            case ThrowStatementSyntax throwStmt:
                result.AddRange(ProcessThrowStatement(throwStmt));
                break;

            case UsingStatementSyntax usingStmt:
                result.AddRange(ProcessUsingStatement(usingStmt));
                break;

            case BlockSyntax block:
                foreach (var stmt in block.Statements)
                {
                    result.AddRange(ProcessStatement(stmt));
                }
                break;

            case EmptyStatementSyntax:
                // Skip
                break;

            case LabeledStatementSyntax labeled:
                result.Add($"{Mapper.SanitizeParagraphName(labeled.Identifier.Text)}.");
                result.AddRange(ProcessStatement(labeled.Statement));
                break;

            case GotoStatementSyntax gotoStmt:
                result.Add($"GO TO {Mapper.SanitizeParagraphName(gotoStmt.Expression.ToString())}");
                break;

            default:
                result.Add($"*> UNSUPPORTED: {statement.Kind()} — {statement.ToString().Replace(Environment.NewLine, " ")}");
                break;
        }

        return result;
    }

    private List<string> ProcessLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
    {
        return ProcessVariableDeclaration(localDecl.Declaration);
    }

    private List<string> ProcessVariableDeclaration(VariableDeclarationSyntax declaration)
    {
        var result = new List<string>();

        foreach (var variable in declaration.Variables)
        {
            string varName = Mapper.SanitizeDataName($"{_currentMethod}-{variable.Identifier.Text}");
            string picClause = GetPicClause(declaration.Type);

            var dataItem = new CobolDataItem
            {
                Level = 01,
                Name = varName,
                Picture = picClause
            };

            if (variable.Initializer != null)
            {
                var initResult = EvaluateExpression(variable.Initializer.Value);
                if (initResult.SetupStatements.Any())
                {
                    result.AddRange(initResult.SetupStatements);
                }

                // Check if the expression is an invocation that returns a value
                if (variable.Initializer.Value is InvocationExpressionSyntax invocation)
                {
                    // The invocation handler should have already created the temp and ACCEPT/CALL
                    // If it's a method call with return, move from return var
                    string methodName = GetMethodName(invocation);
                    if (!string.IsNullOrEmpty(methodName) && methodName != "Console.WriteLine" && methodName != "Console.Write" && methodName != "Console.ReadLine")
                    {
                        string returnVar = Mapper.SanitizeDataName($"{Mapper.SanitizeParagraphName(methodName)}-RETURN");
                        result.Add($"MOVE {returnVar} TO {varName}");
                    }
                    else if (methodName == "Console.ReadLine")
                    {
                        // ACCEPT was already emitted; move from the temp
                        result.Add($"MOVE {initResult.Code} TO {varName}");
                    }
                    else
                    {
                        result.Add($"MOVE {initResult.Code} TO {varName}");
                    }
                }
                else if (variable.Initializer.Value is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
                {
                    result.Add($"COMPUTE {varName} = {initResult.Code}");
                }
                else
                {
                    result.Add($"MOVE {initResult.Code} TO {varName}");
                }
            }

            _program.DataItems.Add(dataItem);
        }

        return result;
    }

    private List<string> ProcessExpressionStatement(ExpressionStatementSyntax exprStmt)
    {
        var result = new List<string>();

        if (exprStmt.Expression is AssignmentExpressionSyntax assignment)
        {
            string target = GetDataName(assignment.Left);
            var rightResult = EvaluateExpression(assignment.Right);
            if (rightResult.SetupStatements.Any())
                result.AddRange(rightResult.SetupStatements);

            if (assignment.Right is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
            {
                result.Add($"COMPUTE {target} = {rightResult.Code}");
            }
            else
            {
                result.Add($"MOVE {rightResult.Code} TO {target}");
            }
        }
        else if (exprStmt.Expression is InvocationExpressionSyntax invocation)
        {
            var invokeResult = ProcessInvocation(invocation);
            result.AddRange(invokeResult);
        }
        else if (exprStmt.Expression is PostfixUnaryExpressionSyntax postfix)
        {
            string operand = GetDataName(postfix.Operand);
            if (postfix.OperatorToken.Text == "++")
                result.Add($"ADD 1 TO {operand}");
            else if (postfix.OperatorToken.Text == "--")
                result.Add($"SUBTRACT 1 FROM {operand}");
        }
        else if (exprStmt.Expression is PrefixUnaryExpressionSyntax prefix)
        {
            string operand = GetDataName(prefix.Operand);
            if (prefix.OperatorToken.Text == "++")
                result.Add($"ADD 1 TO {operand}");
            else if (prefix.OperatorToken.Text == "--")
                result.Add($"SUBTRACT 1 FROM {operand}");
            else if (prefix.OperatorToken.Text == "!")
            {
                // !boolVar — toggle between 0 and 1
                result.Add($"IF {operand} = 1 THEN");
                result.Add($"    MOVE 0 TO {operand}");
                result.Add($"ELSE");
                result.Add($"    MOVE 1 TO {operand}");
                result.Add($"END-IF");
            }
        }

        return result;
    }

    private List<string> ProcessInvocation(InvocationExpressionSyntax invocation)
    {
        var result = new List<string>();
        string methodCall = invocation.Expression.ToString();

        // For special-case matching, keep the dotted form; GetFullMemberName sanitizes to dashed form
        string dottedMethodCall = methodCall;
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            // Also check if it's System.Console.WriteLine etc.
            dottedMethodCall = memberAccess.ToString();
        }

        if (dottedMethodCall == "Console.WriteLine" || dottedMethodCall == "Console.Write" ||
            dottedMethodCall == "System.Console.WriteLine" || dottedMethodCall == "System.Console.Write" ||
            dottedMethodCall.EndsWith(".WriteLine") || dottedMethodCall.EndsWith(".Write"))
        {
            var arg = invocation.ArgumentList.Arguments.FirstOrDefault();
            if (arg != null)
            {
                var exprResult = EvaluateExpression(arg.Expression);
                if (exprResult.SetupStatements.Any())
                    result.AddRange(exprResult.SetupStatements);

                result.Add($"DISPLAY {exprResult.Code}");
            }
            else
            {
                result.Add("DISPLAY SPACE");
            }
        }
        else if (methodCall == "Console.ReadLine" || methodCall.EndsWith(".ReadLine"))
        {
            string tempVar = GetTempVariable("PIC X(255)");
            result.Add($"ACCEPT {tempVar}");
            // Return the temp name so caller can use it
            result.Add($"*> RETURNING: {tempVar}"); // Marker for expression evaluator
        }
        else if (methodCall == "Console.Read" || methodCall.EndsWith(".Read"))
        {
            string tempVar = GetTempVariable("PIC X(1)");
            result.Add($"ACCEPT {tempVar}");
            result.Add($"*> RETURNING: {tempVar}");
        }
        else
        {
            // General method call
            string paragraphName = Mapper.SanitizeParagraphName(methodCall);

            // Build argument list for CALL ... USING
            var args = new List<string>();
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var argResult = EvaluateExpression(arg.Expression);
                if (argResult.SetupStatements.Any())
                    result.AddRange(argResult.SetupStatements);
                args.Add(argResult.Code);
            }

            if (args.Any())
            {
                result.Add($"CALL '{paragraphName}' USING {string.Join(" ", args)}");
            }
            else
            {
                result.Add($"PERFORM {paragraphName}");
            }

            // If the method has a return value, it will be in WS-{paragraphName}-RETURN
        }

        return result;
    }

    private List<string> ProcessIfStatement(IfStatementSyntax ifStmt)
    {
        var result = new List<string>();
        var conditionResult = EvaluateCondition(ifStmt.Condition);
        if (conditionResult.SetupStatements.Any())
            result.AddRange(conditionResult.SetupStatements);

        result.Add($"IF {conditionResult.Code} THEN");

        var thenStatements = ProcessStatement(ifStmt.Statement);
        foreach (var stmt in thenStatements)
            result.Add("    " + stmt);

        if (ifStmt.Else != null)
        {
            result.Add("ELSE");
            var elseStatements = ProcessStatement(ifStmt.Else.Statement);
            foreach (var stmt in elseStatements)
                result.Add("    " + stmt);
        }

        result.Add("END-IF");
        return result;
    }

    private List<string> ProcessSwitchStatement(SwitchStatementSyntax switchStmt)
    {
        var result = new List<string>();
        var exprResult = EvaluateExpression(switchStmt.Expression);
        if (exprResult.SetupStatements.Any())
            result.AddRange(exprResult.SetupStatements);

        result.Add($"EVALUATE {exprResult.Code}");

        foreach (var section in switchStmt.Sections)
        {
            foreach (var label in section.Labels)
            {
                if (label is CaseSwitchLabelSyntax caseLabel)
                {
                    var valResult = EvaluateExpression(caseLabel.Value);
                    if (valResult.SetupStatements.Any())
                        result.AddRange(valResult.SetupStatements);
                    result.Add($"WHEN {valResult.Code}");
                }
                else if (label is DefaultSwitchLabelSyntax)
                {
                    result.Add("WHEN OTHER");
                }
            }

            var bodyStatements = new List<string>();
            foreach (var stmt in section.Statements)
            {
                if (stmt is BreakStatementSyntax) continue;
                bodyStatements.AddRange(ProcessStatement(stmt));
            }

            foreach (var stmt in bodyStatements)
                result.Add("    " + stmt);
        }

        result.Add("END-EVALUATE");
        return result;
    }

    private List<string> ProcessForStatement(ForStatementSyntax forStmt)
    {
        var result = new List<string>();

        // Handle initialization
        if (forStmt.Declaration != null)
        {
            result.AddRange(ProcessVariableDeclaration(forStmt.Declaration));
        }
        else if (forStmt.Initializers.Count > 0)
        {
            foreach (var init in forStmt.Initializers)
            {
                if (init is AssignmentExpressionSyntax assign)
                {
                    string target = GetDataName(assign.Left);
                    var rightResult = EvaluateExpression(assign.Right);
                    if (rightResult.SetupStatements.Any())
                        result.AddRange(rightResult.SetupStatements);

                    if (assign.Right is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
                        result.Add($"COMPUTE {target} = {rightResult.Code}");
                    else
                        result.Add($"MOVE {rightResult.Code} TO {target}");
                }
            }
        }

        // Build loop
        string condition = forStmt.Condition != null
            ? EvaluateCondition(forStmt.Condition).Code
            : "1 = 0";
        string untilCondition = NegateCondition(condition);

        result.Add($"PERFORM UNTIL {untilCondition}");

        // Body
        var bodyStatements = ProcessStatement(forStmt.Statement);
        foreach (var stmt in bodyStatements)
            result.Add("    " + stmt);

        // Incrementors
        foreach (var incr in forStmt.Incrementors)
        {
            if (incr is PostfixUnaryExpressionSyntax postfix)
            {
                string operand = GetDataName(postfix.Operand);
                if (postfix.OperatorToken.Text == "++")
                    result.Add($"    ADD 1 TO {operand}");
                else if (postfix.OperatorToken.Text == "--")
                    result.Add($"    SUBTRACT 1 FROM {operand}");
            }
            else if (incr is PrefixUnaryExpressionSyntax prefix)
            {
                string operand = GetDataName(prefix.Operand);
                if (prefix.OperatorToken.Text == "++")
                    result.Add($"    ADD 1 TO {operand}");
                else if (prefix.OperatorToken.Text == "--")
                    result.Add($"    SUBTRACT 1 FROM {operand}");
            }
            else if (incr is AssignmentExpressionSyntax assign)
            {
                string target = GetDataName(assign.Left);
                var rightResult = EvaluateExpression(assign.Right);
                if (rightResult.SetupStatements.Any())
                    result.AddRange(rightResult.SetupStatements);

                if (assign.Right is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
                    result.Add($"    COMPUTE {target} = {rightResult.Code}");
                else
                    result.Add($"    MOVE {rightResult.Code} TO {target}");
            }
        }

        result.Add("END-PERFORM");
        return result;
    }

    private List<string> ProcessForEachStatement(ForEachStatementSyntax foreachStmt)
    {
        var result = new List<string>();
        string loopVar = Mapper.SanitizeDataName($"{_currentMethod}-{foreachStmt.Identifier.Text}");
        var collectionResult = EvaluateExpression(foreachStmt.Expression);
        if (collectionResult.SetupStatements.Any())
            result.AddRange(collectionResult.SetupStatements);

        // For v1.0.0, we assume the collection is an array or list.
        // We generate an index-based PERFORM VARYING.
        string indexVar = GetTempVariable("PIC S9(9)");
        string limitVar = GetTempVariable("PIC S9(9)");

        // Initialize index
        result.Add($"MOVE 1 TO {indexVar}");
        // For a real implementation, we'd need the array length. 
        // Here we emit a comment and a placeholder limit.
        result.Add($"*> FOREACH over {collectionResult.Code} — array length should be set in {limitVar}");
        result.Add($"PERFORM UNTIL {indexVar} > {limitVar}");

        // Move element: WS-ARRAY(indexVar) TO loopVar
        // This assumes the collection name is directly usable as a COBOL table
        result.Add($"    MOVE {collectionResult.Code}({indexVar}) TO {loopVar}");

        // Body
        var bodyStatements = ProcessStatement(foreachStmt.Statement);
        foreach (var stmt in bodyStatements)
            result.Add("    " + stmt);

        result.Add($"    ADD 1 TO {indexVar}");
        result.Add("END-PERFORM");
        return result;
    }

    private List<string> ProcessWhileStatement(WhileStatementSyntax whileStmt)
    {
        var result = new List<string>();
        var conditionResult = EvaluateCondition(whileStmt.Condition);
        if (conditionResult.SetupStatements.Any())
            result.AddRange(conditionResult.SetupStatements);

        string untilCondition = NegateCondition(conditionResult.Code);
        result.Add($"PERFORM UNTIL {untilCondition}");

        var bodyStatements = ProcessStatement(whileStmt.Statement);
        foreach (var stmt in bodyStatements)
            result.Add("    " + stmt);

        result.Add("END-PERFORM");
        return result;
    }

    private List<string> ProcessDoStatement(DoStatementSyntax doStmt)
    {
        var result = new List<string>();
        var conditionResult = EvaluateCondition(doStmt.Condition);
        if (conditionResult.SetupStatements.Any())
            result.AddRange(conditionResult.SetupStatements);

        string untilCondition = NegateCondition(conditionResult.Code);
        result.Add($"PERFORM WITH TEST AFTER UNTIL {untilCondition}");

        var bodyStatements = ProcessStatement(doStmt.Statement);
        foreach (var stmt in bodyStatements)
            result.Add("    " + stmt);

        result.Add("END-PERFORM");
        return result;
    }

    private List<string> ProcessReturnStatement(ReturnStatementSyntax returnStmt)
    {
        var result = new List<string>();
        if (returnStmt.Expression != null)
        {
            var exprResult = EvaluateExpression(returnStmt.Expression);
            if (exprResult.SetupStatements.Any())
                result.AddRange(exprResult.SetupStatements);

            string returnName = Mapper.SanitizeDataName($"{_currentMethod}-RETURN");

            if (returnStmt.Expression is BinaryExpressionSyntax binExpr &&
                IsArithmeticExpression(binExpr))
            {
                result.Add($"COMPUTE {returnName} = {exprResult.Code}");
            }
            else
            {
                result.Add($"MOVE {exprResult.Code} TO {returnName}");
            }
        }
        result.Add("EXIT");
        return result;
    }

    private List<string> ProcessTryStatement(TryStatementSyntax tryStmt)
    {
        var result = new List<string>();
        result.Add("*> TRY block begin");

        var tryBody = new List<string>();
        foreach (var stmt in tryStmt.Block.Statements)
            tryBody.AddRange(ProcessStatement(stmt));

        // COBOL does not have structured exception handling.
        // We emit the try body and append catch blocks as separate paragraphs with GO TO.
        string endLabel = $"END-TRY-{_tempCounter++}";
        string catchLabel = tryStmt.Catches.Any() ? $"CATCH-{_tempCounter++}" : endLabel;

        // For file/arith operations we could use ON EXCEPTION, but for general try/catch
        // we emit the body and then the catch paragraphs.
        foreach (var stmt in tryBody)
            result.Add(stmt);

        result.Add($"GO TO {endLabel}");

        foreach (var catchClause in tryStmt.Catches)
        {
            result.Add($"{catchLabel}.");
            if (catchClause.Declaration != null)
            {
                string exVar = Mapper.SanitizeDataName($"{_currentMethod}-{catchClause.Declaration.Identifier.Text}");
                result.Add($"*> Exception stored in {exVar}");
            }

            foreach (var stmt in catchClause.Block.Statements)
                result.AddRange(ProcessStatement(stmt));

            if (tryStmt.Catches.IndexOf(catchClause) < tryStmt.Catches.Count - 1)
            {
                string nextCatch = $"CATCH-{_tempCounter++}";
                result.Add($"GO TO {nextCatch}");
                catchLabel = nextCatch;
            }
        }

        if (tryStmt.Finally != null)
        {
            result.Add($"FINALLY-{_tempCounter++}.");
            foreach (var stmt in tryStmt.Finally.Block.Statements)
                result.AddRange(ProcessStatement(stmt));
        }

        result.Add($"{endLabel}.");
        return result;
    }

    private List<string> ProcessThrowStatement(ThrowStatementSyntax throwStmt)
    {
        var result = new List<string>();
        if (throwStmt.Expression != null)
        {
            var exprResult = EvaluateExpression(throwStmt.Expression);
            if (exprResult.SetupStatements.Any())
                result.AddRange(exprResult.SetupStatements);
            result.Add($"*> THROW {exprResult.Code}");
        }
        else
        {
            result.Add(">* RE-THROW current exception");
        }
        // In COBOL, we might GO TO an error paragraph or use RAISE if supported
        result.Add($"GO TO ERROR-HANDLER");
        return result;
    }

    private List<string> ProcessUsingStatement(UsingStatementSyntax usingStmt)
    {
        var result = new List<string>();
        // Map IDisposable to OPEN/CLOSE for streams
        if (usingStmt.Declaration != null)
        {
            result.AddRange(ProcessVariableDeclaration(usingStmt.Declaration));
        }
        else if (usingStmt.Expression != null)
        {
            var exprResult = EvaluateExpression(usingStmt.Expression);
            if (exprResult.SetupStatements.Any())
                result.AddRange(exprResult.SetupStatements);
        }

        // Process body
        //foreach (var stmt in usingStmt.Statement is BlockSyntax block ? block.Statements : new[] { usingStmt.Statement })
        //{
        //    result.AddRange(ProcessStatement(stmt));
        //}
        if (usingStmt.Statement is BlockSyntax block)
        {
            foreach (var stmt in block.Statements)
            {
                result.AddRange(ProcessStatement(stmt));
            }
        }
        else if (usingStmt.Statement != null)
        {
            result.AddRange(ProcessStatement(usingStmt.Statement));
        }

        result.Add("*> DISPOSE / CLOSE resource");
        return result;
    }

    // ==================== EXPRESSION EVALUATION ====================

    private ExpressionResult EvaluateExpression(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return new ExpressionResult { Code = MapLiteral(literal) };

            case IdentifierNameSyntax identifier:
                return new ExpressionResult { Code = GetDataName(identifier) };

            case BinaryExpressionSyntax binExpr:
                return EvaluateBinaryExpression(binExpr);

            case InvocationExpressionSyntax invocation:
                return EvaluateInvocationExpression(invocation);

            case MemberAccessExpressionSyntax memberAccess:
                return EvaluateMemberAccess(memberAccess);

            case ConditionalExpressionSyntax conditional:
                return EvaluateConditionalExpression(conditional);

            case PrefixUnaryExpressionSyntax prefix:
                return EvaluatePrefixExpression(prefix);

            case PostfixUnaryExpressionSyntax postfix:
                return EvaluatePostfixExpression(postfix);

            case ParenthesizedExpressionSyntax paren:
                var inner = EvaluateExpression(paren.Expression);
                inner.Code = $"({inner.Code})";
                return inner;

            case ElementAccessExpressionSyntax elementAccess:
                return EvaluateElementAccess(elementAccess);

            case ObjectCreationExpressionSyntax objectCreation:
                return EvaluateObjectCreation(objectCreation);

            case ArrayCreationExpressionSyntax arrayCreation:
                return EvaluateArrayCreation(arrayCreation);

            case CastExpressionSyntax cast:
                return EvaluateExpression(cast.Expression); // Ignore cast for v1.0.0

            case DefaultExpressionSyntax:
                return new ExpressionResult { Code = "ZERO" };

            case ThisExpressionSyntax:
                return new ExpressionResult { Code = "WS-THIS" };

            case BaseExpressionSyntax:
                return new ExpressionResult { Code = "WS-BASE" };

            default:
                return new ExpressionResult { Code = $"'[{expression.ToString()}]'" };
        }
    }

    private ExpressionResult EvaluateBinaryExpression(BinaryExpressionSyntax binExpr)
    {
        var left = EvaluateExpression(binExpr.Left);
        var right = EvaluateExpression(binExpr.Right);
        string op = Mapper.MapOperator(binExpr.OperatorToken.Text);

        var result = new ExpressionResult();
        result.SetupStatements.AddRange(left.SetupStatements);
        result.SetupStatements.AddRange(right.SetupStatements);

        // Check if this is string concatenation
        if (binExpr.OperatorToken.Text == "+" && IsStringConcatenation(binExpr))
        {
            string tempVar = GetTempVariable("PIC X(1000)");
            var parts = ExtractConcatenationParts(binExpr);
            var sb = new StringBuilder();
            sb.AppendLine($"STRING {parts[0]} DELIMITED BY SIZE");
            for (int i = 1; i < parts.Count; i++)
            {
                sb.AppendLine($"           {parts[i]} DELIMITED BY SIZE");
            }
            sb.AppendLine($"           INTO {tempVar}");
            sb.Append("           END-STRING");
            result.SetupStatements.Add(sb.ToString());
            result.Code = tempVar;
            return result;
        }

        // Check if arithmetic
        if (IsArithmeticOperator(op))
        {
            result.Code = $"{left.Code} {op} {right.Code}";
            return result;
        }

        // Comparison or logical
        result.Code = $"{left.Code} {op} {right.Code}";
        return result;
    }

    private ExpressionResult EvaluateInvocationExpression(InvocationExpressionSyntax invocation)
    {
        var result = new ExpressionResult();
        string methodCall = invocation.Expression.ToString();

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodCall = GetFullMemberName(memberAccess);
        }


        if (methodCall == "Console.ReadLine" || methodCall.EndsWith("ReadLine"))
        {
            string tempVar = GetTempVariable("PIC X(255)");
            result.SetupStatements.Add($"ACCEPT {tempVar}");
            result.Code = tempVar;
            return result;
        }
        else if (methodCall == "Console.Read" ||methodCall.EndsWith("Read"))
        {
            string tempVar = GetTempVariable("PIC X(1)");
            result.SetupStatements.Add($"ACCEPT {tempVar}");
            result.Code = tempVar;
            return result;
        }
        else if (methodCall == "Console.WriteLine" || methodCall.EndsWith("WriteLine") || methodCall.EndsWith("Write"))
        {
            // These are statements, not expressions. If used as expression, we still DISPLAY.
            var arg = invocation.ArgumentList.Arguments.FirstOrDefault();
            if (arg != null)
            {
                var argResult = EvaluateExpression(arg.Expression);
                result.SetupStatements.AddRange(argResult.SetupStatements);
                result.SetupStatements.Add($"DISPLAY {argResult.Code}");
            }
            result.Code = "''"; // Void-like return
            return result;
        }

        // General method call — use CALL
        string paragraphName = Mapper.SanitizeParagraphName(methodCall);
        var args = new List<string>();
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var argResult = EvaluateExpression(arg.Expression);
            result.SetupStatements.AddRange(argResult.SetupStatements);
            args.Add(argResult.Code);
        }

        string returnVar = Mapper.SanitizeDataName($"{paragraphName}-RETURN");
        if (args.Any())
        {
            result.SetupStatements.Add($"CALL '{paragraphName}' USING {string.Join(" ", args)} RETURNING {returnVar}");
        }
        else
        {
            result.SetupStatements.Add($"PERFORM {paragraphName}");
        }
        result.Code = returnVar;
        return result;
    }

    private ExpressionResult EvaluateMemberAccess(MemberAccessExpressionSyntax memberAccess)
    {
        var result = new ExpressionResult();
        string fullName = GetFullMemberName(memberAccess);

        // Handle Console static members
        if (memberAccess.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Console")
        {
            result.Code = fullName; // e.g., "Console.Out" — may need special handling
            return result;
        }

        // Field/property access
        result.Code = Mapper.SanitizeDataName(fullName);
        return result;
    }

    private ExpressionResult EvaluateConditionalExpression(ConditionalExpressionSyntax conditional)
    {
        var result = new ExpressionResult();
        string tempVar = GetTempVariable("PIC X(255)");

        var conditionResult = EvaluateCondition(conditional.Condition);
        var whenTrue = EvaluateExpression(conditional.WhenTrue);
        var whenFalse = EvaluateExpression(conditional.WhenFalse);

        result.SetupStatements.AddRange(conditionResult.SetupStatements);
        result.SetupStatements.Add($"IF {conditionResult.Code} THEN");
        result.SetupStatements.AddRange(whenTrue.SetupStatements.Select(s => "    " + s));
        result.SetupStatements.Add($"    MOVE {whenTrue.Code} TO {tempVar}");
        result.SetupStatements.Add("ELSE");
        result.SetupStatements.AddRange(whenFalse.SetupStatements.Select(s => "    " + s));
        result.SetupStatements.Add($"    MOVE {whenFalse.Code} TO {tempVar}");
        result.SetupStatements.Add("END-IF");

        result.Code = tempVar;
        return result;
    }

    private ExpressionResult EvaluatePrefixExpression(PrefixUnaryExpressionSyntax prefix)
    {
        var result = new ExpressionResult();
        var operand = EvaluateExpression(prefix.Operand);
        result.SetupStatements.AddRange(operand.SetupStatements);

        if (prefix.OperatorToken.Text == "-")
        {
            result.Code = $"-{operand.Code}";
        }
        else if (prefix.OperatorToken.Text == "!")
        {
            result.Code = $"NOT {operand.Code}";
        }
        else if (prefix.OperatorToken.Text == "~")
        {
            result.Code = $"FUNCTION NOT ({operand.Code})";
        }
        else
        {
            result.Code = $"{prefix.OperatorToken.Text}{operand.Code}";
        }
        return result;
    }

    private ExpressionResult EvaluatePostfixExpression(PostfixUnaryExpressionSyntax postfix)
    {
        var result = new ExpressionResult();
        string operand = GetDataName(postfix.Operand);

        if (postfix.OperatorToken.Text == "++")
        {
            result.SetupStatements.Add($"ADD 1 TO {operand}");
            result.Code = operand;
        }
        else if (postfix.OperatorToken.Text == "--")
        {
            result.SetupStatements.Add($"SUBTRACT 1 FROM {operand}");
            result.Code = operand;
        }
        else
        {
            result.Code = operand;
        }
        return result;
    }

    private ExpressionResult EvaluateElementAccess(ElementAccessExpressionSyntax elementAccess)
    {
        var result = new ExpressionResult();
        var baseExpr = EvaluateExpression(elementAccess.Expression);
        result.SetupStatements.AddRange(baseExpr.SetupStatements);

        var index = elementAccess.ArgumentList.Arguments.FirstOrDefault();
        if (index != null)
        {
            var idxResult = EvaluateExpression(index.Expression);
            result.SetupStatements.AddRange(idxResult.SetupStatements);
            result.Code = $"{baseExpr.Code}({idxResult.Code})";
        }
        else
        {
            result.Code = baseExpr.Code;
        }
        return result;
    }

    private ExpressionResult EvaluateObjectCreation(ObjectCreationExpressionSyntax objectCreation)
    {
        var result = new ExpressionResult();
        string typeName = objectCreation.Type.ToString();
        string tempVar = GetTempVariable("PIC X(255)");
        result.SetupStatements.Add($"*> NEW {typeName}() — object creation not fully supported in v1.0.0");
        result.SetupStatements.Add($"INITIALIZE {tempVar}");
        result.Code = tempVar;
        return result;
    }

    private ExpressionResult EvaluateArrayCreation(ArrayCreationExpressionSyntax arrayCreation)
    {
        var result = new ExpressionResult();
        string tempVar = GetTempVariable("PIC X(255)");
        result.SetupStatements.Add($"*> ARRAY creation — initialize table structure");
        result.SetupStatements.Add($"INITIALIZE {tempVar}");
        result.Code = tempVar;
        return result;
    }

    // ==================== CONDITION EVALUATION ====================

    private ExpressionResult EvaluateCondition(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case BinaryExpressionSyntax binExpr:
                var left = EvaluateExpression(binExpr.Left);
                var right = EvaluateExpression(binExpr.Right);
                string op = Mapper.MapOperator(binExpr.OperatorToken.Text);
                var result = new ExpressionResult();
                result.SetupStatements.AddRange(left.SetupStatements);
                result.SetupStatements.AddRange(right.SetupStatements);
                result.Code = $"{left.Code} {op} {right.Code}";
                return result;

            case LiteralExpressionSyntax literal:
                return EvaluateExpression(literal);

            case IdentifierNameSyntax identifier:
                return new ExpressionResult { Code = GetDataName(identifier) };

            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
                var operand = EvaluateCondition(prefix.Operand);
                return new ExpressionResult
                {
                    SetupStatements = operand.SetupStatements,
                    Code = $"NOT {operand.Code}"
                };

            case ParenthesizedExpressionSyntax paren:
                var inner = EvaluateCondition(paren.Expression);
                inner.Code = $"({inner.Code})";
                return inner;

            default:
                var general = EvaluateExpression(expression);
                return general;
        }
    }

    // ==================== HELPERS ====================

    private string GetDataName(ExpressionSyntax expression)
    {
        if (expression is IdentifierNameSyntax identifier)
        {
            return Mapper.SanitizeDataName($"{_currentMethod}-{identifier.Identifier.Text}");
        }
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            return Mapper.SanitizeDataName(GetFullMemberName(memberAccess));
        }
        if (expression is ElementAccessExpressionSyntax elementAccess)
        {
            var baseName = GetDataName(elementAccess.Expression);
            var index = elementAccess.ArgumentList.Arguments.FirstOrDefault();
            if (index != null)
            {
                var idxResult = EvaluateExpression(index.Expression);
                return $"{baseName}({idxResult.Code})";
            }
            return baseName;
        }
        return Mapper.SanitizeDataName(expression.ToString());
    }

    private string GetFullMemberName(MemberAccessExpressionSyntax memberAccess)
    {
        var parts = new List<string>();
        ExpressionSyntax current = memberAccess;
        while (current is MemberAccessExpressionSyntax ma)
        {
            parts.Insert(0, ma.Name.Identifier.Text);
            current = ma.Expression;
        }
        if (current is IdentifierNameSyntax id)
        {
            parts.Insert(0, id.Identifier.Text);
        }
        return string.Join("-", parts);
    }

    private string GetMethodName(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            return GetFullMemberName(memberAccess);
        if (invocation.Expression is IdentifierNameSyntax identifier)
            return identifier.Identifier.Text;
        return invocation.Expression.ToString();
    }

    private string GetPicClause(TypeSyntax type)
    {
        if (_semanticModel != null)
        {
            try
            {
                var typeInfo = _semanticModel.GetTypeInfo(type);
                if (typeInfo.Type != null)
                {
                    return Mapper.MapType(typeInfo.Type);
                }
            }
            catch (ArgumentException)
            {
                // Syntax node not in this semantic model's tree; fall back to syntax-based mapping
            }
        }
        return Mapper.MapType(type);
    }

    private string MapLiteral(LiteralExpressionSyntax literal)
    {
        return Mapper.MapLiteral(literal);
    }

    private bool IsStringConcatenation(BinaryExpressionSyntax binExpr)
    {
        if (binExpr.OperatorToken.Text != "+") return false;

        if (_semanticModel != null)
        {
            var typeInfo = _semanticModel.GetTypeInfo(binExpr);
            return typeInfo.Type?.SpecialType == SpecialType.System_String;
        }

        // Fallback heuristic
        if (binExpr.Left is LiteralExpressionSyntax litLeft && litLeft.IsKind(SyntaxKind.StringLiteralExpression))
            return true;
        if (binExpr.Right is LiteralExpressionSyntax litRight && litRight.IsKind(SyntaxKind.StringLiteralExpression))
            return true;

        return false;
    }

    private bool IsArithmeticExpression(BinaryExpressionSyntax binExpr)
    {
        if (binExpr.OperatorToken.Text == "+")
            return !IsStringConcatenation(binExpr);

        return IsArithmeticOperator(Mapper.MapOperator(binExpr.OperatorToken.Text));
    }

    private bool IsArithmeticOperator(string op)
    {
        return op == "+" || op == "-" || op == "*" || op == "/" || op == "FUNCTION MOD";
    }

    private List<string> ExtractConcatenationParts(BinaryExpressionSyntax binExpr)
    {
        var parts = new List<string>();

        if (binExpr.Left is BinaryExpressionSyntax leftBin && IsStringConcatenation(leftBin))
        {
            parts.AddRange(ExtractConcatenationParts(leftBin));
        }
        else
        {
            var leftResult = EvaluateExpression(binExpr.Left);
            parts.Add(leftResult.Code);
        }

        if (binExpr.Right is BinaryExpressionSyntax rightBin && IsStringConcatenation(rightBin))
        {
            parts.AddRange(ExtractConcatenationParts(rightBin));
        }
        else
        {
            var rightResult = EvaluateExpression(binExpr.Right);
            parts.Add(rightResult.Code);
        }

        return parts;
    }

    private string NegateCondition(string condition)
    {
        if (condition.Contains(" = "))
            return condition.Replace(" = ", " <> ");
        if (condition.Contains(" <> "))
            return condition.Replace(" <> ", " = ");
        if (condition.Contains(" < "))
            return condition.Replace(" < ", " >= ");
        if (condition.Contains(" > "))
            return condition.Replace(" > ", " <= ");
        if (condition.Contains(" <= "))
            return condition.Replace(" <= ", " > ");
        if (condition.Contains(" >= "))
            return condition.Replace(" >= ", " < ");

        return $"NOT ({condition})";
    }

    private string GetTempVariable(string picture = "PIC S9(9)")
    {
        var name = $"WS-TEMP-{_tempCounter++:D3}";
        _program.DataItems.Add(new CobolDataItem
        {
            Level = 01,
            Name = name,
            Picture = picture
        });
        return name;
    }

    private void ExtractComments(SyntaxTriviaList triviaList)
    {
        foreach (var trivia in triviaList)
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                string comment = trivia.ToString().Replace("//", "").Replace("/*", "").Replace("*/", "").Trim();
                if (!string.IsNullOrWhiteSpace(comment))
                    _pendingComments.Add(comment);
            }
        }
    }

    private string FormatConstantValue(object value)
    {
        return value switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "1" : "0",
            decimal d => d.ToString(),
            double db => db.ToString(),
            float f => f.ToString(),
            char c => $"'{c}'",
            _ => value?.ToString() ?? "ZERO"
        };
    }
}

//public class Parser
//{
//    private CobolProgram _program;
//    private string _currentMethod;
//    private int _tempCounter;
//    private SemanticModel _semanticModel;
//    private readonly List<string> _pendingComments = new();

//    public Parser(SemanticModel semanticModel = null)
//    {
//        _semanticModel = semanticModel;
//    }

//    public CobolProgram Parse(CompilationUnitSyntax root, string programId)
//    {
//        _program = new CobolProgram();
//        _program.ProgramId = programId ?? "CSHARP2CBL";
//        _program.DataItems = new List<CobolDataItem>();
//        _program.FileItems = new List<CobolFileItem>();
//        _program.Paragraphs = new List<CobolParagraph>();
//        _program.MainStatements = new List<string>();
//        _tempCounter = 0;

//        // Extract comments from leading trivia of the compilation unit
//        ExtractComments(root.GetLeadingTrivia());

//        // Handle top-level statements (C# 9+)
//        var topLevelStatements = root.Members.OfType<GlobalStatementSyntax>().ToList();
//        if (topLevelStatements.Any())
//        {
//            ProcessTopLevelStatements(topLevelStatements);
//        }

//        // Extract namespace and class info
//        var namespaceDecl = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
//        var fileScopedNs = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
//        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

//        if (classDecl != null && string.IsNullOrEmpty(programId))
//        {
//            _program.ProgramId = Mapper.SanitizeProgramId(classDecl.Identifier.Text);
//        }

//        // Process class-level members (fields, properties, methods)
//        if (classDecl != null)
//        {
//            ProcessClassMembers(classDecl);
//        }
//        else if (!topLevelStatements.Any())
//        {
//            // No class and no top-level statements — try to find methods at root level (unlikely but possible)
//            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
//            foreach (var method in methods)
//            {
//                ProcessMethod(method);
//            }
//        }

//        // Prepend initialization statements to MAIN
//        if (_program.InitializationStatements.Any())
//        {
//            if (_program.MainStatements.Any())
//            {
//                _program.MainStatements.InsertRange(0, _program.InitializationStatements);
//            }
//            else if (_program.Paragraphs.Any(p => p.Name == "MAIN"))
//            {
//                var mainPara = _program.Paragraphs.First(p => p.Name == "MAIN");
//                mainPara.Statements.InsertRange(0, _program.InitializationStatements);
//            }
//        }

//        // If no explicit paragraphs were created but we have main statements, ensure MAIN exists
//        if (_program.Paragraphs.Count == 0 && _program.MainStatements.Count > 0)
//        {
//            _program.Paragraphs.Add(new CobolParagraph
//            {
//                Name = "MAIN",
//                Statements = _program.MainStatements
//            });
//        }
//        else if (_program.MainStatements.Count > 0 && _program.Paragraphs.Any(p => p.Name == "MAIN"))
//        {
//            // Merge initialization into existing MAIN
//            var mainPara = _program.Paragraphs.First(p => p.Name == "MAIN");
//            mainPara.Statements.InsertRange(0, _program.MainStatements);
//        }

//        return _program;
//    }

//    private void ProcessTopLevelStatements(List<GlobalStatementSyntax> topLevelStatements)
//    {
//        _currentMethod = "MAIN";
//        var paragraph = new CobolParagraph
//        {
//            Name = "MAIN",
//            Statements = new List<string>()
//        };

//        foreach (var globalStmt in topLevelStatements)
//        {
//            ExtractComments(globalStmt.GetLeadingTrivia());
//            if (_pendingComments.Any())
//            {
//                paragraph.Comments.AddRange(_pendingComments);
//                _pendingComments.Clear();
//            }

//            var stmts = ProcessStatement(globalStmt.Statement);
//            paragraph.Statements.AddRange(stmts);
//        }

//        paragraph.Statements.Add("STOP RUN");
//        _program.MainStatements = paragraph.Statements;
//        _program.Paragraphs.Add(paragraph);
//    }

//    private void ProcessClassMembers(ClassDeclarationSyntax classDecl)
//    {
//        foreach (var member in classDecl.Members)
//        {
//            ExtractComments(member.GetLeadingTrivia());

//            switch (member)
//            {
//                case FieldDeclarationSyntax field:
//                    ProcessField(field);
//                    break;
//                case PropertyDeclarationSyntax prop:
//                    ProcessProperty(prop);
//                    break;
//                case MethodDeclarationSyntax method:
//                    ProcessMethod(method);
//                    break;
//                case ConstructorDeclarationSyntax ctor:
//                    ProcessConstructor(ctor);
//                    break;
//            }
//        }
//    }

//    private void ProcessField(FieldDeclarationSyntax field)
//    {
//        string picClause = GetPicClause(field.Declaration.Type);
//        bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
//        bool isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword);

//        foreach (var variable in field.Declaration.Variables)
//        {
//            string varName = Mapper.SanitizeDataName(variable.Identifier.Text);
//            var dataItem = new CobolDataItem
//            {
//                Level = 01,
//                Name = varName,
//                Picture = picClause
//            };

//            if (variable.Initializer != null)
//            {
//                var result = EvaluateExpression(variable.Initializer.Value);
//                if (result.SetupStatements.Any())
//                {
//                    _program.InitializationStatements.AddRange(result.SetupStatements);
//                }
//                dataItem.Value = result.Code;
//            }
//            else if (isConst)
//            {
//                // Try to get constant value from semantic model
//                if (_semanticModel != null && variable.Initializer != null)
//                {
//                    var constValue = _semanticModel.GetConstantValue(variable.Initializer.Value);
//                    if (constValue.HasValue && constValue.Value != null)
//                    {
//                        dataItem.Value = FormatConstantValue(constValue.Value);
//                    }
//                }
//            }

//            if (_pendingComments.Any())
//            {
//                dataItem.Comments.AddRange(_pendingComments);
//                _pendingComments.Clear();
//            }

//            _program.DataItems.Add(dataItem);
//        }
//    }

//    private void ProcessProperty(PropertyDeclarationSyntax prop)
//    {
//        // Auto-properties become WORKING-STORAGE items
//        // For v1.0.0, we treat properties as fields (backing field generated implicitly)
//        string varName = Mapper.SanitizeDataName(prop.Identifier.Text);
//        string picClause = GetPicClause(prop.Type);

//        var dataItem = new CobolDataItem
//        {
//            Level = 01,
//            Name = varName,
//            Picture = picClause
//        };

//        if (prop.Initializer != null)
//        {
//            var result = EvaluateExpression(prop.Initializer.Value);
//            if (result.SetupStatements.Any())
//            {
//                _program.InitializationStatements.AddRange(result.SetupStatements);
//            }
//            dataItem.Value = result.Code;
//        }

//        _program.DataItems.Add(dataItem);
//    }

//    private void ProcessConstructor(ConstructorDeclarationSyntax ctor)
//    {
//        // Constructors map to an initialization paragraph
//        string ctorName = Mapper.SanitizeParagraphName($"INIT-{ctor.Identifier.Text}");
//        var paragraph = new CobolParagraph
//        {
//            Name = ctorName,
//            Statements = new List<string>()
//        };

//        if (ctor.Body != null)
//        {
//            foreach (var statement in ctor.Body.Statements)
//            {
//                paragraph.Statements.AddRange(ProcessStatement(statement));
//            }
//        }
//        else if (ctor.ExpressionBody != null)
//        {
//            // Expression-bodied constructor
//            var result = EvaluateExpression(ctor.ExpressionBody.Expression);
//            if (result.SetupStatements.Any())
//                paragraph.Statements.AddRange(result.SetupStatements);
//        }

//        _program.Paragraphs.Add(paragraph);
//        _program.InitializationStatements.Add($"PERFORM {ctorName}");
//    }

//    private void ProcessMethod(MethodDeclarationSyntax method)
//    {
//        string methodName = Mapper.SanitizeParagraphName(method.Identifier.Text);
//        _currentMethod = methodName;

//        var paragraph = new CobolParagraph
//        {
//            Name = methodName,
//            Statements = new List<string>()
//        };

//        // Process parameters as data items (method-scoped working storage)
//        foreach (var param in method.ParameterList.Parameters)
//        {
//            string dataName = Mapper.SanitizeDataName($"{methodName}-{param.Identifier.Text}");
//            string picClause = GetPicClause(param.Type);
//            _program.DataItems.Add(new CobolDataItem
//            {
//                Level = 01,
//                Name = dataName,
//                Picture = picClause
//            });
//        }

//        // Handle return type
//        if (method.ReturnType.ToString() != "void")
//        {
//            string returnName = Mapper.SanitizeDataName($"{methodName}-RETURN");
//            string returnPic = GetPicClause(method.ReturnType);
//            _program.DataItems.Add(new CobolDataItem
//            {
//                Level = 01,
//                Name = returnName,
//                Picture = returnPic
//            });
//        }

//        // Process method body
//        if (method.Body != null)
//        {
//            foreach (var statement in method.Body.Statements)
//            {
//                var stmts = ProcessStatement(statement);
//                paragraph.Statements.AddRange(stmts);
//            }
//        }
//        else if (method.ExpressionBody != null)
//        {
//            // Expression-bodied method: result = expression; EXIT
//            var result = EvaluateExpression(method.ExpressionBody.Expression);
//            if (result.SetupStatements.Any())
//                paragraph.Statements.AddRange(result.SetupStatements);

//            string returnName = Mapper.SanitizeDataName($"{methodName}-RETURN");
//            paragraph.Statements.Add($"MOVE {result.Code} TO {returnName}");
//        }

//        // Add termination
//        if (methodName == "MAIN" || method.Identifier.Text == "Main")
//        {
//            paragraph.Statements.Add("STOP RUN");
//            _program.MainStatements = paragraph.Statements;
//        }
//        else
//        {
//            paragraph.Statements.Add("EXIT");
//            _program.Paragraphs.Add(paragraph);
//        }
//    }

//    private List<string> ProcessStatement(StatementSyntax statement)
//    {
//        var result = new List<string>();
//        ExtractComments(statement.GetLeadingTrivia());

//        if (_pendingComments.Any())
//        {
//            foreach (var comment in _pendingComments)
//                result.Add($"*> {comment}");
//            _pendingComments.Clear();
//        }

//        switch (statement)
//        {
//            case LocalDeclarationStatementSyntax localDecl:
//                result.AddRange(ProcessLocalDeclaration(localDecl));
//                break;

//            case ExpressionStatementSyntax exprStmt:
//                result.AddRange(ProcessExpressionStatement(exprStmt));
//                break;

//            case IfStatementSyntax ifStmt:
//                result.AddRange(ProcessIfStatement(ifStmt));
//                break;

//            case SwitchStatementSyntax switchStmt:
//                result.AddRange(ProcessSwitchStatement(switchStmt));
//                break;

//            case ForStatementSyntax forStmt:
//                result.AddRange(ProcessForStatement(forStmt));
//                break;

//            case ForEachStatementSyntax foreachStmt:
//                result.AddRange(ProcessForEachStatement(foreachStmt));
//                break;

//            case WhileStatementSyntax whileStmt:
//                result.AddRange(ProcessWhileStatement(whileStmt));
//                break;

//            case DoStatementSyntax doStmt:
//                result.AddRange(ProcessDoStatement(doStmt));
//                break;

//            case ReturnStatementSyntax returnStmt:
//                result.AddRange(ProcessReturnStatement(returnStmt));
//                break;

//            case BreakStatementSyntax:
//                result.Add("EXIT PERFORM");
//                break;

//            case ContinueStatementSyntax:
//                result.Add("*> CONTINUE — next iteration");
//                break;

//            case TryStatementSyntax tryStmt:
//                result.AddRange(ProcessTryStatement(tryStmt));
//                break;

//            case ThrowStatementSyntax throwStmt:
//                result.AddRange(ProcessThrowStatement(throwStmt));
//                break;

//            case UsingStatementSyntax usingStmt:
//                result.AddRange(ProcessUsingStatement(usingStmt));
//                break;

//            case BlockSyntax block:
//                foreach (var stmt in block.Statements)
//                {
//                    result.AddRange(ProcessStatement(stmt));
//                }
//                break;

//            case EmptyStatementSyntax:
//                // Skip
//                break;

//            case LabeledStatementSyntax labeled:
//                result.Add($"{Mapper.SanitizeParagraphName(labeled.Identifier.Text)}.");
//                result.AddRange(ProcessStatement(labeled.Statement));
//                break;

//            case GotoStatementSyntax gotoStmt:
//                result.Add($"GO TO {Mapper.SanitizeParagraphName(gotoStmt.Expression.ToString())}");
//                break;

//            default:
//                result.Add($"*> UNSUPPORTED: {statement.Kind()} — {statement.ToString().Replace(Environment.NewLine, " ")}");
//                break;
//        }

//        return result;
//    }

//    private List<string> ProcessLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
//    {
//        return ProcessVariableDeclaration(localDecl.Declaration);
//    }

//    private List<string> ProcessVariableDeclaration(VariableDeclarationSyntax declaration)
//    {
//        var result = new List<string>();

//        foreach (var variable in declaration.Variables)
//        {
//            string varName = Mapper.SanitizeDataName($"{_currentMethod}-{variable.Identifier.Text}");
//            string picClause = GetPicClause(declaration.Type);

//            var dataItem = new CobolDataItem
//            {
//                Level = 01,
//                Name = varName,
//                Picture = picClause
//            };

//            if (variable.Initializer != null)
//            {
//                var initResult = EvaluateExpression(variable.Initializer.Value);
//                if (initResult.SetupStatements.Any())
//                {
//                    result.AddRange(initResult.SetupStatements);
//                }

//                // Check if the expression is an invocation that returns a value
//                if (variable.Initializer.Value is InvocationExpressionSyntax invocation)
//                {
//                    // The invocation handler should have already created the temp and ACCEPT/CALL
//                    // If it's a method call with return, move from return var
//                    string methodName = GetMethodName(invocation);
//                    if (!string.IsNullOrEmpty(methodName) && methodName != "Console.WriteLine" && methodName != "Console.Write" && methodName != "Console.ReadLine")
//                    {
//                        string returnVar = Mapper.SanitizeDataName($"{Mapper.SanitizeParagraphName(methodName)}-RETURN");
//                        result.Add($"MOVE {returnVar} TO {varName}");
//                    }
//                    else if (methodName == "Console.ReadLine")
//                    {
//                        // ACCEPT was already emitted; move from the temp
//                        result.Add($"MOVE {initResult.Code} TO {varName}");
//                    }
//                    else
//                    {
//                        result.Add($"MOVE {initResult.Code} TO {varName}");
//                    }
//                }
//                else if (variable.Initializer.Value is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
//                {
//                    result.Add($"COMPUTE {varName} = {initResult.Code}");
//                }
//                else
//                {
//                    result.Add($"MOVE {initResult.Code} TO {varName}");
//                }
//            }

//            _program.DataItems.Add(dataItem);
//        }

//        return result;
//    }

//    private List<string> ProcessExpressionStatement(ExpressionStatementSyntax exprStmt)
//    {
//        var result = new List<string>();

//        if (exprStmt.Expression is AssignmentExpressionSyntax assignment)
//        {
//            string target = GetDataName(assignment.Left);
//            var rightResult = EvaluateExpression(assignment.Right);
//            if (rightResult.SetupStatements.Any())
//                result.AddRange(rightResult.SetupStatements);

//            if (assignment.Right is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
//            {
//                result.Add($"COMPUTE {target} = {rightResult.Code}");
//            }
//            else
//            {
//                result.Add($"MOVE {rightResult.Code} TO {target}");
//            }
//        }
//        else if (exprStmt.Expression is InvocationExpressionSyntax invocation)
//        {
//            var invokeResult = ProcessInvocation(invocation);
//            result.AddRange(invokeResult);
//        }
//        else if (exprStmt.Expression is PostfixUnaryExpressionSyntax postfix)
//        {
//            string operand = GetDataName(postfix.Operand);
//            if (postfix.OperatorToken.Text == "++")
//                result.Add($"ADD 1 TO {operand}");
//            else if (postfix.OperatorToken.Text == "--")
//                result.Add($"SUBTRACT 1 FROM {operand}");
//        }
//        else if (exprStmt.Expression is PrefixUnaryExpressionSyntax prefix)
//        {
//            string operand = GetDataName(prefix.Operand);
//            if (prefix.OperatorToken.Text == "++")
//                result.Add($"ADD 1 TO {operand}");
//            else if (prefix.OperatorToken.Text == "--")
//                result.Add($"SUBTRACT 1 FROM {operand}");
//            else if (prefix.OperatorToken.Text == "!")
//            {
//                // !boolVar — toggle between 0 and 1
//                result.Add($"IF {operand} = 1 THEN");
//                result.Add($"    MOVE 0 TO {operand}");
//                result.Add($"ELSE");
//                result.Add($"    MOVE 1 TO {operand}");
//                result.Add($"END-IF");
//            }
//        }

//        return result;
//    }

//    private List<string> ProcessInvocation(InvocationExpressionSyntax invocation)
//    {
//        var result = new List<string>();
//        string methodCall = invocation.Expression.ToString();

//        // Handle qualified names like System.Console.WriteLine
//        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
//        {
//            methodCall = GetFullMemberName(memberAccess);
//        }

//        if (methodCall == "Console.WriteLine" || methodCall == "Console.Write" || methodCall.EndsWith(".WriteLine") || methodCall.EndsWith(".Write"))
//        {
//            var arg = invocation.ArgumentList.Arguments.FirstOrDefault();
//            if (arg != null)
//            {
//                var exprResult = EvaluateExpression(arg.Expression);
//                if (exprResult.SetupStatements.Any())
//                    result.AddRange(exprResult.SetupStatements);

//                result.Add($"DISPLAY {exprResult.Code}");
//            }
//            else
//            {
//                result.Add("DISPLAY SPACE");
//            }
//        }
//        else if (methodCall == "Console.ReadLine" || methodCall.EndsWith(".ReadLine"))
//        {
//            string tempVar = GetTempVariable("PIC X(255)");
//            result.Add($"ACCEPT {tempVar}");
//            // Return the temp name so caller can use it
//            result.Add($"*> RETURNING: {tempVar}"); // Marker for expression evaluator
//        }
//        else if (methodCall == "Console.Read" || methodCall.EndsWith(".Read"))
//        {
//            string tempVar = GetTempVariable("PIC X(1)");
//            result.Add($"ACCEPT {tempVar}");
//            result.Add($"*> RETURNING: {tempVar}");
//        }
//        else
//        {
//            // General method call
//            string paragraphName = Mapper.SanitizeParagraphName(methodCall);

//            // Build argument list for CALL ... USING
//            var args = new List<string>();
//            foreach (var arg in invocation.ArgumentList.Arguments)
//            {
//                var argResult = EvaluateExpression(arg.Expression);
//                if (argResult.SetupStatements.Any())
//                    result.AddRange(argResult.SetupStatements);
//                args.Add(argResult.Code);
//            }

//            if (args.Any())
//            {
//                result.Add($"CALL '{paragraphName}' USING {string.Join(" ", args)}");
//            }
//            else
//            {
//                result.Add($"PERFORM {paragraphName}");
//            }

//            // If the method has a return value, it will be in WS-{paragraphName}-RETURN
//        }

//        return result;
//    }

//    private List<string> ProcessIfStatement(IfStatementSyntax ifStmt)
//    {
//        var result = new List<string>();
//        var conditionResult = EvaluateCondition(ifStmt.Condition);
//        if (conditionResult.SetupStatements.Any())
//            result.AddRange(conditionResult.SetupStatements);

//        result.Add($"IF {conditionResult.Code} THEN");

//        var thenStatements = ProcessStatement(ifStmt.Statement);
//        foreach (var stmt in thenStatements)
//            result.Add("    " + stmt);

//        if (ifStmt.Else != null)
//        {
//            result.Add("ELSE");
//            var elseStatements = ProcessStatement(ifStmt.Else.Statement);
//            foreach (var stmt in elseStatements)
//                result.Add("    " + stmt);
//        }

//        result.Add("END-IF");
//        return result;
//    }

//    private List<string> ProcessSwitchStatement(SwitchStatementSyntax switchStmt)
//    {
//        var result = new List<string>();
//        var exprResult = EvaluateExpression(switchStmt.Expression);
//        if (exprResult.SetupStatements.Any())
//            result.AddRange(exprResult.SetupStatements);

//        result.Add($"EVALUATE {exprResult.Code}");

//        foreach (var section in switchStmt.Sections)
//        {
//            foreach (var label in section.Labels)
//            {
//                if (label is CaseSwitchLabelSyntax caseLabel)
//                {
//                    var valResult = EvaluateExpression(caseLabel.Value);
//                    if (valResult.SetupStatements.Any())
//                        result.AddRange(valResult.SetupStatements);
//                    result.Add($"WHEN {valResult.Code}");
//                }
//                else if (label is DefaultSwitchLabelSyntax)
//                {
//                    result.Add("WHEN OTHER");
//                }
//            }

//            var bodyStatements = new List<string>();
//            foreach (var stmt in section.Statements)
//            {
//                if (stmt is BreakStatementSyntax) continue;
//                bodyStatements.AddRange(ProcessStatement(stmt));
//            }

//            foreach (var stmt in bodyStatements)
//                result.Add("    " + stmt);
//        }

//        result.Add("END-EVALUATE");
//        return result;
//    }

//    private List<string> ProcessForStatement(ForStatementSyntax forStmt)
//    {
//        var result = new List<string>();

//        // Handle initialization
//        if (forStmt.Declaration != null)
//        {
//            result.AddRange(ProcessVariableDeclaration(forStmt.Declaration));
//        }
//        else if (forStmt.Initializers.Count > 0)
//        {
//            foreach (var init in forStmt.Initializers)
//            {
//                if (init is AssignmentExpressionSyntax assign)
//                {
//                    string target = GetDataName(assign.Left);
//                    var rightResult = EvaluateExpression(assign.Right);
//                    if (rightResult.SetupStatements.Any())
//                        result.AddRange(rightResult.SetupStatements);

//                    if (assign.Right is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
//                        result.Add($"COMPUTE {target} = {rightResult.Code}");
//                    else
//                        result.Add($"MOVE {rightResult.Code} TO {target}");
//                }
//            }
//        }

//        // Build loop
//        string condition = forStmt.Condition != null
//            ? EvaluateCondition(forStmt.Condition).Code
//            : "1 = 0";
//        string untilCondition = NegateCondition(condition);

//        result.Add($"PERFORM UNTIL {untilCondition}");

//        // Body
//        var bodyStatements = ProcessStatement(forStmt.Statement);
//        foreach (var stmt in bodyStatements)
//            result.Add("    " + stmt);

//        // Incrementors
//        foreach (var incr in forStmt.Incrementors)
//        {
//            if (incr is PostfixUnaryExpressionSyntax postfix)
//            {
//                string operand = GetDataName(postfix.Operand);
//                if (postfix.OperatorToken.Text == "++")
//                    result.Add($"    ADD 1 TO {operand}");
//                else if (postfix.OperatorToken.Text == "--")
//                    result.Add($"    SUBTRACT 1 FROM {operand}");
//            }
//            else if (incr is PrefixUnaryExpressionSyntax prefix)
//            {
//                string operand = GetDataName(prefix.Operand);
//                if (prefix.OperatorToken.Text == "++")
//                    result.Add($"    ADD 1 TO {operand}");
//                else if (prefix.OperatorToken.Text == "--")
//                    result.Add($"    SUBTRACT 1 FROM {operand}");
//            }
//            else if (incr is AssignmentExpressionSyntax assign)
//            {
//                string target = GetDataName(assign.Left);
//                var rightResult = EvaluateExpression(assign.Right);
//                if (rightResult.SetupStatements.Any())
//                    result.AddRange(rightResult.SetupStatements);

//                if (assign.Right is BinaryExpressionSyntax binExpr && IsArithmeticExpression(binExpr))
//                    result.Add($"    COMPUTE {target} = {rightResult.Code}");
//                else
//                    result.Add($"    MOVE {rightResult.Code} TO {target}");
//            }
//        }

//        result.Add("END-PERFORM");
//        return result;
//    }

//    private List<string> ProcessForEachStatement(ForEachStatementSyntax foreachStmt)
//    {
//        var result = new List<string>();
//        string loopVar = Mapper.SanitizeDataName($"{_currentMethod}-{foreachStmt.Identifier.Text}");
//        var collectionResult = EvaluateExpression(foreachStmt.Expression);
//        if (collectionResult.SetupStatements.Any())
//            result.AddRange(collectionResult.SetupStatements);

//        // For v1.0.0, we assume the collection is an array or list.
//        // We generate an index-based PERFORM VARYING.
//        string indexVar = GetTempVariable("PIC S9(9)");
//        string limitVar = GetTempVariable("PIC S9(9)");

//        // Initialize index
//        result.Add($"MOVE 1 TO {indexVar}");
//        // For a real implementation, we'd need the array length. 
//        // Here we emit a comment and a placeholder limit.
//        result.Add($"*> FOREACH over {collectionResult.Code} — array length should be set in {limitVar}");
//        result.Add($"PERFORM UNTIL {indexVar} > {limitVar}");

//        // Move element: WS-ARRAY(indexVar) TO loopVar
//        // This assumes the collection name is directly usable as a COBOL table
//        result.Add($"    MOVE {collectionResult.Code}({indexVar}) TO {loopVar}");

//        // Body
//        var bodyStatements = ProcessStatement(foreachStmt.Statement);
//        foreach (var stmt in bodyStatements)
//            result.Add("    " + stmt);

//        result.Add($"    ADD 1 TO {indexVar}");
//        result.Add("END-PERFORM");
//        return result;
//    }

//    private List<string> ProcessWhileStatement(WhileStatementSyntax whileStmt)
//    {
//        var result = new List<string>();
//        var conditionResult = EvaluateCondition(whileStmt.Condition);
//        if (conditionResult.SetupStatements.Any())
//            result.AddRange(conditionResult.SetupStatements);

//        string untilCondition = NegateCondition(conditionResult.Code);
//        result.Add($"PERFORM UNTIL {untilCondition}");

//        var bodyStatements = ProcessStatement(whileStmt.Statement);
//        foreach (var stmt in bodyStatements)
//            result.Add("    " + stmt);

//        result.Add("END-PERFORM");
//        return result;
//    }

//    private List<string> ProcessDoStatement(DoStatementSyntax doStmt)
//    {
//        var result = new List<string>();
//        var conditionResult = EvaluateCondition(doStmt.Condition);
//        if (conditionResult.SetupStatements.Any())
//            result.AddRange(conditionResult.SetupStatements);

//        string untilCondition = NegateCondition(conditionResult.Code);
//        result.Add($"PERFORM WITH TEST AFTER UNTIL {untilCondition}");

//        var bodyStatements = ProcessStatement(doStmt.Statement);
//        foreach (var stmt in bodyStatements)
//            result.Add("    " + stmt);

//        result.Add("END-PERFORM");
//        return result;
//    }

//    private List<string> ProcessReturnStatement(ReturnStatementSyntax returnStmt)
//    {
//        var result = new List<string>();
//        if (returnStmt.Expression != null)
//        {
//            var exprResult = EvaluateExpression(returnStmt.Expression);
//            if (exprResult.SetupStatements.Any())
//                result.AddRange(exprResult.SetupStatements);

//            string returnName = Mapper.SanitizeDataName($"{_currentMethod}-RETURN");
//            result.Add($"MOVE {exprResult.Code} TO {returnName}");
//        }
//        result.Add("EXIT");
//        return result;
//    }

//    private List<string> ProcessTryStatement(TryStatementSyntax tryStmt)
//    {
//        var result = new List<string>();
//        result.Add("*> TRY block begin");

//        var tryBody = new List<string>();
//        foreach (var stmt in tryStmt.Block.Statements)
//            tryBody.AddRange(ProcessStatement(stmt));

//        // COBOL does not have structured exception handling.
//        // We emit the try body and append catch blocks as separate paragraphs with GO TO.
//        string endLabel = $"END-TRY-{_tempCounter++}";
//        string catchLabel = tryStmt.Catches.Any() ? $"CATCH-{_tempCounter++}" : endLabel;

//        // For file/arith operations we could use ON EXCEPTION, but for general try/catch
//        // we emit the body and then the catch paragraphs.
//        foreach (var stmt in tryBody)
//            result.Add(stmt);

//        result.Add($"GO TO {endLabel}");

//        foreach (var catchClause in tryStmt.Catches)
//        {
//            result.Add($"{catchLabel}.");
//            if (catchClause.Declaration != null)
//            {
//                string exVar = Mapper.SanitizeDataName($"{_currentMethod}-{catchClause.Declaration.Identifier.Text}");
//                result.Add($"*> Exception stored in {exVar}");
//            }

//            foreach (var stmt in catchClause.Block.Statements)
//                result.AddRange(ProcessStatement(stmt));

//            if (tryStmt.Catches.IndexOf(catchClause) < tryStmt.Catches.Count - 1)
//            {
//                string nextCatch = $"CATCH-{_tempCounter++}";
//                result.Add($"GO TO {nextCatch}");
//                catchLabel = nextCatch;
//            }
//        }

//        if (tryStmt.Finally != null)
//        {
//            result.Add($"FINALLY-{_tempCounter++}.");
//            foreach (var stmt in tryStmt.Finally.Block.Statements)
//                result.AddRange(ProcessStatement(stmt));
//        }

//        result.Add($"{endLabel}.");
//        return result;
//    }

//    private List<string> ProcessThrowStatement(ThrowStatementSyntax throwStmt)
//    {
//        var result = new List<string>();
//        if (throwStmt.Expression != null)
//        {
//            var exprResult = EvaluateExpression(throwStmt.Expression);
//            if (exprResult.SetupStatements.Any())
//                result.AddRange(exprResult.SetupStatements);
//            result.Add($"*> THROW {exprResult.Code}");
//        }
//        else
//        {
//            result.Add(">* RE-THROW current exception");
//        }
//        // In COBOL, we might GO TO an error paragraph or use RAISE if supported
//        result.Add($"GO TO ERROR-HANDLER");
//        return result;
//    }

//    //private List<string> ProcessUsingStatement(UsingStatementSyntax usingStmt)
//    //{
//    //    var result = new List<string>();
//    //    // Map IDisposable to OPEN/CLOSE for streams
//    //    if (usingStmt.Declaration != null)
//    //    {
//    //        result.AddRange(ProcessVariableDeclaration(usingStmt.Declaration));
//    //    }
//    //    else if (usingStmt.Expression != null)
//    //    {
//    //        var exprResult = EvaluateExpression(usingStmt.Expression);
//    //        if (exprResult.SetupStatements.Any())
//    //            result.AddRange(exprResult.SetupStatements);
//    //    }

//    //    // Process body
//    //    foreach (var stmt in usingStmt.Statement is BlockSyntax block ? block.Statements : new[] { usingStmt.Statement })
//    //    {
//    //        result.AddRange(ProcessStatement(stmt));
//    //    }

//    //    result.Add("*> DISPOSE / CLOSE resource");
//    //    return result;
//    //}
//    private List<string> ProcessUsingStatement(UsingStatementSyntax usingStmt)
//    {
//        var result = new List<string>();

//        // Map IDisposable to OPEN/CLOSE for streams
//        if (usingStmt.Declaration != null)
//        {
//            result.AddRange(ProcessVariableDeclaration(usingStmt.Declaration));
//        }
//        else if (usingStmt.Expression != null)
//        {
//            var exprResult = EvaluateExpression(usingStmt.Expression);
//            if (exprResult.SetupStatements.Any())
//            {
//                result.AddRange(exprResult.SetupStatements);
//            }
//        }

//        // Process body
//        if (usingStmt.Statement is BlockSyntax block)
//        {
//            foreach (var stmt in block.Statements)
//            {
//                result.AddRange(ProcessStatement(stmt));
//            }
//        }
//        else if (usingStmt.Statement != null)
//        {
//            result.AddRange(ProcessStatement(usingStmt.Statement));
//        }

//        result.Add("*> DISPOSE / CLOSE resource");
//        return result;
//    }

//    // ==================== EXPRESSION EVALUATION ====================

//    private ExpressionResult EvaluateExpression(ExpressionSyntax expression)
//    {
//        switch (expression)
//        {
//            case LiteralExpressionSyntax literal:
//                return new ExpressionResult { Code = MapLiteral(literal) };

//            case IdentifierNameSyntax identifier:
//                return new ExpressionResult { Code = GetDataName(identifier) };

//            case BinaryExpressionSyntax binExpr:
//                return EvaluateBinaryExpression(binExpr);

//            case InvocationExpressionSyntax invocation:
//                return EvaluateInvocationExpression(invocation);

//            case MemberAccessExpressionSyntax memberAccess:
//                return EvaluateMemberAccess(memberAccess);

//            case ConditionalExpressionSyntax conditional:
//                return EvaluateConditionalExpression(conditional);

//            case PrefixUnaryExpressionSyntax prefix:
//                return EvaluatePrefixExpression(prefix);

//            case PostfixUnaryExpressionSyntax postfix:
//                return EvaluatePostfixExpression(postfix);

//            case ParenthesizedExpressionSyntax paren:
//                var inner = EvaluateExpression(paren.Expression);
//                inner.Code = $"({inner.Code})";
//                return inner;

//            case ElementAccessExpressionSyntax elementAccess:
//                return EvaluateElementAccess(elementAccess);

//            case ObjectCreationExpressionSyntax objectCreation:
//                return EvaluateObjectCreation(objectCreation);

//            case ArrayCreationExpressionSyntax arrayCreation:
//                return EvaluateArrayCreation(arrayCreation);

//            case CastExpressionSyntax cast:
//                return EvaluateExpression(cast.Expression); // Ignore cast for v1.0.0

//            case DefaultExpressionSyntax:
//                return new ExpressionResult { Code = "ZERO" };

//            case ThisExpressionSyntax:
//                return new ExpressionResult { Code = "WS-THIS" }; 

//            case BaseExpressionSyntax:
//                return new ExpressionResult { Code = "WS-BASE" }; 

//            default:
//                return new ExpressionResult { Code = $"'[{expression.ToString()}]'" };
//        }
//    }

//    private ExpressionResult EvaluateBinaryExpression(BinaryExpressionSyntax binExpr)
//    {
//        var left = EvaluateExpression(binExpr.Left);
//        var right = EvaluateExpression(binExpr.Right);
//        string op = Mapper.MapOperator(binExpr.OperatorToken.Text);

//        var result = new ExpressionResult();
//        result.SetupStatements.AddRange(left.SetupStatements);
//        result.SetupStatements.AddRange(right.SetupStatements);

//        // Check if this is string concatenation
//        if (binExpr.OperatorToken.Text == "+" && IsStringConcatenation(binExpr))
//        {
//            string tempVar = GetTempVariable("PIC X(1000)");
//            var parts = ExtractConcatenationParts(binExpr);
//            var sb = new StringBuilder();
//            sb.AppendLine($"STRING {parts[0]} DELIMITED BY SIZE");
//            for (int i = 1; i < parts.Count; i++)
//            {
//                sb.AppendLine($"           {parts[i]} DELIMITED BY SIZE");
//            }
//            sb.AppendLine($"           INTO {tempVar}");
//            sb.Append("           END-STRING");
//            result.SetupStatements.Add(sb.ToString());
//            result.Code = tempVar;
//            return result;
//        }

//        // Check if arithmetic
//        if (IsArithmeticOperator(op))
//        {
//            result.Code = $"{left.Code} {op} {right.Code}";
//            return result;
//        }

//        // Comparison or logical
//        result.Code = $"{left.Code} {op} {right.Code}";
//        return result;
//    }

//    private ExpressionResult EvaluateInvocationExpression(InvocationExpressionSyntax invocation)
//    {
//        var result = new ExpressionResult();
//        string methodCall = invocation.Expression.ToString();

//        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
//        {
//            methodCall = GetFullMemberName(memberAccess);
//        }

//        if (methodCall == "Console.ReadLine" || methodCall.EndsWith(".ReadLine"))
//        {
//            string tempVar = GetTempVariable("PIC X(255)");
//            result.SetupStatements.Add($"ACCEPT {tempVar}");
//            result.Code = tempVar;
//            return result;
//        }
//        else if (methodCall == "Console.Read" || methodCall.EndsWith(".Read"))
//        {
//            string tempVar = GetTempVariable("PIC X(1)");
//            result.SetupStatements.Add($"ACCEPT {tempVar}");
//            result.Code = tempVar;
//            return result;
//        }
//        else if (methodCall == "Console.WriteLine" || methodCall == "Console.Write" || methodCall.EndsWith(".WriteLine") || methodCall.EndsWith(".Write"))
//        {
//            // These are statements, not expressions. If used as expression, we still DISPLAY.
//            var arg = invocation.ArgumentList.Arguments.FirstOrDefault();
//            if (arg != null)
//            {
//                var argResult = EvaluateExpression(arg.Expression);
//                result.SetupStatements.AddRange(argResult.SetupStatements);
//                result.SetupStatements.Add($"DISPLAY {argResult.Code}");
//            }
//            result.Code = "''"; // Void-like return
//            return result;
//        }

//        // General method call — use CALL
//        string paragraphName = Mapper.SanitizeParagraphName(methodCall);
//        var args = new List<string>();
//        foreach (var arg in invocation.ArgumentList.Arguments)
//        {
//            var argResult = EvaluateExpression(arg.Expression);
//            result.SetupStatements.AddRange(argResult.SetupStatements);
//            args.Add(argResult.Code);
//        }

//        string returnVar = Mapper.SanitizeDataName($"{paragraphName}-RETURN");
//        if (args.Any())
//        {
//            result.SetupStatements.Add($"CALL '{paragraphName}' USING {string.Join(" ", args)} RETURNING {returnVar}");
//        }
//        else
//        {
//            result.SetupStatements.Add($"PERFORM {paragraphName}");
//        }
//        result.Code = returnVar;
//        return result;
//    }

//    private ExpressionResult EvaluateMemberAccess(MemberAccessExpressionSyntax memberAccess)
//    {
//        var result = new ExpressionResult();
//        string fullName = GetFullMemberName(memberAccess);

//        // Handle Console static members
//        if (memberAccess.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Console")
//        {
//            result.Code = fullName; // e.g., "Console.Out" — may need special handling
//            return result;
//        }

//        // Field/property access
//        result.Code = Mapper.SanitizeDataName(fullName);
//        return result;
//    }

//    private ExpressionResult EvaluateConditionalExpression(ConditionalExpressionSyntax conditional)
//    {
//        var result = new ExpressionResult();
//        string tempVar = GetTempVariable("PIC X(255)");

//        var conditionResult = EvaluateCondition(conditional.Condition);
//        var whenTrue = EvaluateExpression(conditional.WhenTrue);
//        var whenFalse = EvaluateExpression(conditional.WhenFalse);

//        result.SetupStatements.AddRange(conditionResult.SetupStatements);
//        result.SetupStatements.Add($"IF {conditionResult.Code} THEN");
//        result.SetupStatements.AddRange(whenTrue.SetupStatements.Select(s => "    " + s));
//        result.SetupStatements.Add($"    MOVE {whenTrue.Code} TO {tempVar}");
//        result.SetupStatements.Add("ELSE");
//        result.SetupStatements.AddRange(whenFalse.SetupStatements.Select(s => "    " + s));
//        result.SetupStatements.Add($"    MOVE {whenFalse.Code} TO {tempVar}");
//        result.SetupStatements.Add("END-IF");

//        result.Code = tempVar;
//        return result;
//    }

//    private ExpressionResult EvaluatePrefixExpression(PrefixUnaryExpressionSyntax prefix)
//    {
//        var result = new ExpressionResult();
//        var operand = EvaluateExpression(prefix.Operand);
//        result.SetupStatements.AddRange(operand.SetupStatements);

//        if (prefix.OperatorToken.Text == "-")
//        {
//            result.Code = $"-{operand.Code}";
//        }
//        else if (prefix.OperatorToken.Text == "!")
//        {
//            result.Code = $"NOT {operand.Code}";
//        }
//        else if (prefix.OperatorToken.Text == "~")
//        {
//            result.Code = $"FUNCTION NOT ({operand.Code})";
//        }
//        else
//        {
//            result.Code = $"{prefix.OperatorToken.Text}{operand.Code}";
//        }
//        return result;
//    }

//    private ExpressionResult EvaluatePostfixExpression(PostfixUnaryExpressionSyntax postfix)
//    {
//        var result = new ExpressionResult();
//        string operand = GetDataName(postfix.Operand);

//        if (postfix.OperatorToken.Text == "++")
//        {
//            result.SetupStatements.Add($"ADD 1 TO {operand}");
//            result.Code = operand;
//        }
//        else if (postfix.OperatorToken.Text == "--")
//        {
//            result.SetupStatements.Add($"SUBTRACT 1 FROM {operand}");
//            result.Code = operand;
//        }
//        else
//        {
//            result.Code = operand;
//        }
//        return result;
//    }

//    private ExpressionResult EvaluateElementAccess(ElementAccessExpressionSyntax elementAccess)
//    {
//        var result = new ExpressionResult();
//        var baseExpr = EvaluateExpression(elementAccess.Expression);
//        result.SetupStatements.AddRange(baseExpr.SetupStatements);

//        var index = elementAccess.ArgumentList.Arguments.FirstOrDefault();
//        if (index != null)
//        {
//            var idxResult = EvaluateExpression(index.Expression);
//            result.SetupStatements.AddRange(idxResult.SetupStatements);
//            result.Code = $"{baseExpr.Code}({idxResult.Code})";
//        }
//        else
//        {
//            result.Code = baseExpr.Code;
//        }
//        return result;
//    }

//    private ExpressionResult EvaluateObjectCreation(ObjectCreationExpressionSyntax objectCreation)
//    {
//        var result = new ExpressionResult();
//        string typeName = objectCreation.Type.ToString();
//        string tempVar = GetTempVariable("PIC X(255)");
//        result.SetupStatements.Add($"*> NEW {typeName}() — object creation not fully supported in v1.0.0");
//        result.SetupStatements.Add($"INITIALIZE {tempVar}");
//        result.Code = tempVar;
//        return result;
//    }

//    private ExpressionResult EvaluateArrayCreation(ArrayCreationExpressionSyntax arrayCreation)
//    {
//        var result = new ExpressionResult();
//        string tempVar = GetTempVariable("PIC X(255)");
//        result.SetupStatements.Add($"*> ARRAY creation — initialize table structure");
//        result.SetupStatements.Add($"INITIALIZE {tempVar}");
//        result.Code = tempVar;
//        return result;
//    }

//    // ==================== CONDITION EVALUATION ====================

//    private ExpressionResult EvaluateCondition(ExpressionSyntax expression)
//    {
//        switch (expression)
//        {
//            case BinaryExpressionSyntax binExpr:
//                var left = EvaluateExpression(binExpr.Left);
//                var right = EvaluateExpression(binExpr.Right);
//                string op = Mapper.MapOperator(binExpr.OperatorToken.Text);
//                var result = new ExpressionResult();
//                result.SetupStatements.AddRange(left.SetupStatements);
//                result.SetupStatements.AddRange(right.SetupStatements);
//                result.Code = $"{left.Code} {op} {right.Code}";
//                return result;

//            case LiteralExpressionSyntax literal:
//                return EvaluateExpression(literal);

//            case IdentifierNameSyntax identifier:
//                return new ExpressionResult { Code = GetDataName(identifier) };

//            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
//                var operand = EvaluateCondition(prefix.Operand);
//                return new ExpressionResult
//                {
//                    SetupStatements = operand.SetupStatements,
//                    Code = $"NOT {operand.Code}"
//                };

//            case ParenthesizedExpressionSyntax paren:
//                var inner = EvaluateCondition(paren.Expression);
//                inner.Code = $"({inner.Code})";
//                return inner;

//            default:
//                var general = EvaluateExpression(expression);
//                return general;
//        }
//    }

//    // ==================== HELPERS ====================

//    private string GetDataName(ExpressionSyntax expression)
//    {
//        if (expression is IdentifierNameSyntax identifier)
//        {
//            return Mapper.SanitizeDataName($"{_currentMethod}-{identifier.Identifier.Text}");
//        }
//        if (expression is MemberAccessExpressionSyntax memberAccess)
//        {
//            return Mapper.SanitizeDataName(GetFullMemberName(memberAccess));
//        }
//        if (expression is ElementAccessExpressionSyntax elementAccess)
//        {
//            var baseName = GetDataName(elementAccess.Expression);
//            var index = elementAccess.ArgumentList.Arguments.FirstOrDefault();
//            if (index != null)
//            {
//                var idxResult = EvaluateExpression(index.Expression);
//                return $"{baseName}({idxResult.Code})";
//            }
//            return baseName;
//        }
//        return Mapper.SanitizeDataName(expression.ToString());
//    }

//    private string GetFullMemberName(MemberAccessExpressionSyntax memberAccess)
//    {
//        var parts = new List<string>();
//        ExpressionSyntax current = memberAccess;
//        while (current is MemberAccessExpressionSyntax ma)
//        {
//            parts.Insert(0, ma.Name.Identifier.Text);
//            current = ma.Expression;
//        }
//        if (current is IdentifierNameSyntax id)
//        {
//            parts.Insert(0, id.Identifier.Text);
//        }
//        return string.Join("-", parts);
//    }

//    private string GetMethodName(InvocationExpressionSyntax invocation)
//    {
//        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
//            return GetFullMemberName(memberAccess);
//        if (invocation.Expression is IdentifierNameSyntax identifier)
//            return identifier.Identifier.Text;
//        return invocation.Expression.ToString();
//    }

//    private string GetPicClause(TypeSyntax type)
//    {
//        if (_semanticModel != null)
//        {
//            var typeInfo = _semanticModel.GetTypeInfo(type);
//            if (typeInfo.Type != null)
//            {
//                return Mapper.MapType(typeInfo.Type);
//            }
//        }
//        return Mapper.MapType(type);
//    }

//    private string MapLiteral(LiteralExpressionSyntax literal)
//    {
//        return Mapper.MapLiteral(literal);
//    }

//    private bool IsStringConcatenation(BinaryExpressionSyntax binExpr)
//    {
//        if (binExpr.OperatorToken.Text != "+") return false;

//        if (_semanticModel != null)
//        {
//            var typeInfo = _semanticModel.GetTypeInfo(binExpr);
//            return typeInfo.Type?.SpecialType == SpecialType.System_String;
//        }

//        // Fallback heuristic
//        if (binExpr.Left is LiteralExpressionSyntax litLeft && litLeft.IsKind(SyntaxKind.StringLiteralExpression))
//            return true;
//        if (binExpr.Right is LiteralExpressionSyntax litRight && litRight.IsKind(SyntaxKind.StringLiteralExpression))
//            return true;

//        return false;
//    }

//    private bool IsArithmeticExpression(BinaryExpressionSyntax binExpr)
//    {
//        if (binExpr.OperatorToken.Text == "+")
//            return !IsStringConcatenation(binExpr);

//        return IsArithmeticOperator(Mapper.MapOperator(binExpr.OperatorToken.Text));
//    }

//    private bool IsArithmeticOperator(string op)
//    {
//        return op == "+" || op == "-" || op == "*" || op == "/" || op == "FUNCTION MOD";
//    }

//    private List<string> ExtractConcatenationParts(BinaryExpressionSyntax binExpr)
//    {
//        var parts = new List<string>();

//        if (binExpr.Left is BinaryExpressionSyntax leftBin && IsStringConcatenation(leftBin))
//        {
//            parts.AddRange(ExtractConcatenationParts(leftBin));
//        }
//        else
//        {
//            var leftResult = EvaluateExpression(binExpr.Left);
//            parts.Add(leftResult.Code);
//        }

//        if (binExpr.Right is BinaryExpressionSyntax rightBin && IsStringConcatenation(rightBin))
//        {
//            parts.AddRange(ExtractConcatenationParts(rightBin));
//        }
//        else
//        {
//            var rightResult = EvaluateExpression(binExpr.Right);
//            parts.Add(rightResult.Code);
//        }

//        return parts;
//    }

//    private string NegateCondition(string condition)
//    {
//        if (condition.Contains(" = "))
//            return condition.Replace(" = ", " <> ");
//        if (condition.Contains(" <> "))
//            return condition.Replace(" <> ", " = ");
//        if (condition.Contains(" < "))
//            return condition.Replace(" < ", " >= ");
//        if (condition.Contains(" > "))
//            return condition.Replace(" > ", " <= ");
//        if (condition.Contains(" <= "))
//            return condition.Replace(" <= ", " > ");
//        if (condition.Contains(" >= "))
//            return condition.Replace(" >= ", " < ");

//        return $"NOT ({condition})";
//    }

//    private string GetTempVariable(string picture = "PIC S9(9)")
//    {
//        var name = $"WS-TEMP-{_tempCounter++:D3}";
//        _program.DataItems.Add(new CobolDataItem
//        {
//            Level = 01,
//            Name = name,
//            Picture = picture
//        });
//        return name;
//    }

//    private void ExtractComments(SyntaxTriviaList triviaList)
//    {
//        foreach (var trivia in triviaList)
//        {
//            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
//                trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
//            {
//                string comment = trivia.ToString().Replace("//", "").Replace("/*", "").Replace("*/", "").Trim();
//                if (!string.IsNullOrWhiteSpace(comment))
//                    _pendingComments.Add(comment);
//            }
//        }
//    }

//    private string FormatConstantValue(object value)
//    {
//        return value switch
//        {
//            int i => i.ToString(),
//            long l => l.ToString(),
//            string s => $"'{s.Replace("'", "''")}'",
//            bool b => b ? "1" : "0",
//            decimal d => d.ToString(),
//            double db => db.ToString(),
//            float f => f.ToString(),
//            char c => $"'{c}'",
//            _ => value?.ToString() ?? "ZERO"
//        };
//    }
//}
