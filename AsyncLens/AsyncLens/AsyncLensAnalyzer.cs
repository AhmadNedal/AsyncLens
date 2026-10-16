using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Threading;

namespace AsyncLens
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AsyncLensAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ASYNC001";
        public const string SleepDiagnosticId = "ASYNC002";
        public const string AsyncVoidDiagnosticId = "ASYNC003";

        private static readonly DiagnosticDescriptor BlockingRule =
            new DiagnosticDescriptor(
                id: DiagnosticId,
                title: "Avoid blocking on asynchronous code",
                messageFormat: "Avoid blocking on a Task. Consider using 'await' instead.",
                category: "Async",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                description: "Blocking on a Task can cause thread starvation or deadlocks in some contexts.");

        private static readonly DiagnosticDescriptor SleepRule =
            new DiagnosticDescriptor(
                id: SleepDiagnosticId,
                title: "Avoid Thread.Sleep in async methods",
                messageFormat: "Avoid 'Thread.Sleep' in an async method. Use 'await Task.Delay' instead.",
                category: "Async",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                description: "Thread.Sleep blocks the current thread instead of yielding it back to the caller.");

        private static readonly DiagnosticDescriptor AsyncVoidRule =
            new DiagnosticDescriptor(
                id: AsyncVoidDiagnosticId,
                title: "Avoid async void methods",
                messageFormat: "Method '{0}' is 'async void'. Exceptions cannot be observed by the caller; return 'Task' instead.",
                category: "Async",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                description: "Async void methods should only be used for event handlers.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(BlockingRule, SleepRule, AsyncVoidRule); }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task") == null)
                    return;

                start.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
                start.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
                start.RegisterSymbolAction(AnalyzeMethodSymbol, SymbolKind.Method);
            });
        }

        // task.Result  (Task<T> / ValueTask<T>)
        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var memberAccess = (MemberAccessExpressionSyntax)context.Node;

            if (memberAccess.Name.Identifier.ValueText != "Result")
                return;

            var property = context.SemanticModel
                .GetSymbolInfo(memberAccess, context.CancellationToken).Symbol as IPropertySymbol;

            if (!AsyncLensHelpers.IsTaskResult(property))
                return;

            var receiver = AsyncLensHelpers.Unwrap(memberAccess.Expression);
            if (IsKnownCompleted(context.SemanticModel, receiver, memberAccess, context.CancellationToken))
                return;

            context.ReportDiagnostic(Diagnostic.Create(BlockingRule, memberAccess.Name.GetLocation()));
        }

        // task.Wait(...), Task.WaitAll/WaitAny, task.GetAwaiter().GetResult(), Thread.Sleep(...)
        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
            if (memberAccess == null)
                return;

            string name = memberAccess.Name.Identifier.ValueText;
            if (name != "Wait" && name != "WaitAll" && name != "WaitAny" &&
                name != "GetResult" && name != "Sleep")
                return;

            var method = context.SemanticModel
                .GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;

            if (method == null)
                return;

            var location = memberAccess.Name.GetLocation();

            switch (name)
            {
                case "Sleep":
                    if (AsyncLensHelpers.IsThreadSleep(method) &&
                        AsyncLensHelpers.IsInsideAsyncFunction(invocation))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SleepRule, location));
                    }
                    break;

                case "WaitAll":
                case "WaitAny":
                    if (AsyncLensHelpers.IsTaskWaitAllOrAny(method))
                        context.ReportDiagnostic(Diagnostic.Create(BlockingRule, location));
                    break;

                case "Wait":
                    if (AsyncLensHelpers.IsTaskWait(method))
                    {
                        var receiver = AsyncLensHelpers.Unwrap(memberAccess.Expression);
                        if (!IsKnownCompleted(context.SemanticModel, receiver, invocation, context.CancellationToken))
                            context.ReportDiagnostic(Diagnostic.Create(BlockingRule, location));
                    }
                    break;

                case "GetResult":
                    if (AsyncLensHelpers.IsAwaiterGetResult(method))
                    {
                        var receiver = GetAwaitedReceiver(memberAccess);
                        if (receiver == null ||
                            !IsKnownCompleted(context.SemanticModel, receiver, invocation, context.CancellationToken))
                            context.ReportDiagnostic(Diagnostic.Create(BlockingRule, location));
                    }
                    break;
            }
        }

        // async void (outside event handlers)
        private static void AnalyzeMethodSymbol(SymbolAnalysisContext context)
        {
            var method = (IMethodSymbol)context.Symbol;

            if (!method.IsAsync || !method.ReturnsVoid || method.MethodKind != MethodKind.Ordinary)
                return;

            if (method.IsOverride ||
                method.ExplicitInterfaceImplementations.Length > 0 ||
                AsyncLensHelpers.ImplementsInterfaceMember(method))
                return;

            if (IsEventHandlerShape(method))
                return;

            if (method.Locations.Length == 0 || !method.Locations[0].IsInSource)
                return;

            context.ReportDiagnostic(Diagnostic.Create(AsyncVoidRule, method.Locations[0], method.Name));
        }

        private static bool IsEventHandlerShape(IMethodSymbol method)
        {
            if (method.Parameters.Length != 2)
                return false;

            for (var type = method.Parameters[1].Type; type != null; type = type.BaseType)
            {
                if (type.Name == "EventArgs" && type.ContainingNamespace?.ToDisplayString() == "System")
                    return true;
            }

            return false;
        }

        // t.GetAwaiter().GetResult()  ->  t   (ConfigureAwait is stripped)
        private static ExpressionSyntax GetAwaitedReceiver(MemberAccessExpressionSyntax getResultAccess)
        {
            var getAwaiterCall = getResultAccess.Expression as InvocationExpressionSyntax;
            var getAwaiterAccess = getAwaiterCall == null
                ? null
                : getAwaiterCall.Expression as MemberAccessExpressionSyntax;

            if (getAwaiterAccess == null || getAwaiterAccess.Name.Identifier.ValueText != "GetAwaiter")
                return null;

            return AsyncLensHelpers.Unwrap(getAwaiterAccess.Expression);
        }

        // ------------------------------------------------------------ "already completed" detection

        private static bool IsKnownCompleted(
            SemanticModel model, ExpressionSyntax receiver, SyntaxNode usage, CancellationToken ct)
        {
            if (receiver == null)
                return false;

            // Task.FromResult(..) / Task.FromException(..) / Task.FromCanceled(..)
            var invocation = receiver as InvocationExpressionSyntax;
            if (invocation != null)
            {
                var m = model.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
                return m != null &&
                       m.IsStatic &&
                       AsyncLensHelpers.IsType(m.ContainingType, AsyncLensHelpers.TasksNs, "Task", 0) &&
                       (m.Name == "FromResult" || m.Name == "FromException" || m.Name == "FromCanceled");
            }

            // Task.CompletedTask
            var memberAccess = receiver as MemberAccessExpressionSyntax;
            if (memberAccess != null)
            {
                var p = model.GetSymbolInfo(memberAccess, ct).Symbol as IPropertySymbol;
                return p != null &&
                       p.IsStatic &&
                       p.Name == "CompletedTask" &&
                       AsyncLensHelpers.IsType(p.ContainingType, AsyncLensHelpers.TasksNs, "Task", 0);
            }

            var identifier = receiver as IdentifierNameSyntax;
            if (identifier != null)
            {
                var symbol = model.GetSymbolInfo(identifier, ct).Symbol;
                if (symbol == null)
                    return false;

                return IsAwaitedEarlier(model, symbol, usage, ct) ||
                       IsContinueWithParameter(model, symbol, usage, ct);
            }

            return false;
        }

        // 'await t;' or 'await Task.WhenAll(t, ...)' as a statement that always runs before the usage
        // (a previous statement of the same block or of an enclosing block).
        private static bool IsAwaitedEarlier(
            SemanticModel model, ISymbol symbol, SyntaxNode usage, CancellationToken ct)
        {
            if (!(symbol is ILocalSymbol) && !(symbol is IParameterSymbol))
                return false;

            SyntaxNode child = usage;
            foreach (var ancestor in usage.Ancestors())
            {
                var block = ancestor as BlockSyntax;
                if (block != null)
                {
                    foreach (var statement in block.Statements)
                    {
                        if (statement == child)
                            break;

                        if (StatementAwaits(statement, symbol, model, ct))
                            return true;
                    }
                }

                if (ancestor is AnonymousFunctionExpressionSyntax ||
                    ancestor is LocalFunctionStatementSyntax ||
                    ancestor is BaseMethodDeclarationSyntax)
                    break;

                child = ancestor;
            }

            return false;
        }

        private static bool StatementAwaits(
            StatementSyntax statement, ISymbol symbol, SemanticModel model, CancellationToken ct)
        {
            ExpressionSyntax expression = null;

            var expressionStatement = statement as ExpressionStatementSyntax;
            if (expressionStatement != null)
            {
                expression = expressionStatement.Expression;
            }
            else
            {
                var declaration = statement as LocalDeclarationStatementSyntax;
                if (declaration != null && declaration.Declaration.Variables.Count == 1)
                {
                    var initializer = declaration.Declaration.Variables[0].Initializer;
                    expression = initializer == null ? null : initializer.Value;
                }
            }

            var assignment = expression as AssignmentExpressionSyntax;
            if (assignment != null)
                expression = assignment.Right;

            var awaitExpression = expression as AwaitExpressionSyntax;
            return awaitExpression != null && AwaitCovers(awaitExpression, symbol, model, ct);
        }

        private static bool AwaitCovers(
            AwaitExpressionSyntax awaitExpression, ISymbol symbol, SemanticModel model, CancellationToken ct)
        {
            var operand = AsyncLensHelpers.Unwrap(awaitExpression.Expression);

            var identifier = operand as IdentifierNameSyntax;
            if (identifier != null)
                return SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, ct).Symbol, symbol);

            var invocation = operand as InvocationExpressionSyntax;
            if (invocation != null)
            {
                var m = model.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
                if (m != null && m.IsStatic && m.Name == "WhenAll" &&
                    AsyncLensHelpers.IsType(m.ContainingType, AsyncLensHelpers.TasksNs, "Task", 0))
                {
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        var argumentId = argument.Expression as IdentifierNameSyntax;
                        if (argumentId != null &&
                            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(argumentId, ct).Symbol, symbol))
                            return true;
                    }
                }
            }

            return false;
        }

        // task.ContinueWith(t => t.Result): 't' is already completed inside the continuation.
        private static bool IsContinueWithParameter(
            SemanticModel model, ISymbol symbol, SyntaxNode usage, CancellationToken ct)
        {
            var parameter = symbol as IParameterSymbol;
            if (parameter == null)
                return false;

            foreach (var ancestor in usage.Ancestors())
            {
                var lambda = ancestor as AnonymousFunctionExpressionSyntax;
                if (lambda == null)
                    continue;

                var lambdaSymbol = model.GetSymbolInfo(lambda, ct).Symbol as IMethodSymbol;
                if (lambdaSymbol == null ||
                    lambdaSymbol.Parameters.Length == 0 ||
                    !SymbolEqualityComparer.Default.Equals(lambdaSymbol.Parameters[0], parameter))
                    continue;

                var argument = lambda.Parent as ArgumentSyntax;
                var call = argument == null || argument.Parent == null
                    ? null
                    : argument.Parent.Parent as InvocationExpressionSyntax;
                if (call == null)
                    return false;

                var callMethod = model.GetSymbolInfo(call, ct).Symbol as IMethodSymbol;
                return callMethod != null &&
                       callMethod.Name == "ContinueWith" &&
                       AsyncLensHelpers.IsTaskOrTaskOfT(callMethod.ContainingType);
            }

            return false;
        }
    }
}