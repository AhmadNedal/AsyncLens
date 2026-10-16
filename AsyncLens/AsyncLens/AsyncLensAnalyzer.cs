
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace AsyncLens
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AsyncLensAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ASYNC001";

        private static readonly DiagnosticDescriptor Rule =
            new DiagnosticDescriptor(
                id: DiagnosticId,
                title: "Avoid blocking on asynchronous code",
                messageFormat: "Avoid blocking on a Task. Consider using 'await' instead.",
                category: "Async",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                description: "Blocking on a Task can cause thread starvation or deadlocks in some contexts.");

        public override ImmutableArray<DiagnosticDescriptor>
            SupportedDiagnostics
        {
            get { return ImmutableArray.Create(Rule); }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(
                GeneratedCodeAnalysisFlags.None);

            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(
                AnalyzeMemberAccess,
                SyntaxKind.SimpleMemberAccessExpression);

            context.RegisterSyntaxNodeAction(
                AnalyzeInvocation,
                SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeMemberAccess(
            SyntaxNodeAnalysisContext context)
        {
            var memberAccess =
                (MemberAccessExpressionSyntax)context.Node;

            if (memberAccess.Name.Identifier.ValueText != "Result")
                return;

            var type = context.SemanticModel.GetTypeInfo(
                memberAccess.Expression,
                context.CancellationToken).Type;

            var namedType = type as INamedTypeSymbol;

            if (namedType == null ||
                !IsTaskType(namedType) ||
                namedType.TypeArguments.Length != 1)
            {
                return;
            }

            Report(context, memberAccess.Name.GetLocation());
        }

        private static void AnalyzeInvocation(
            SyntaxNodeAnalysisContext context)
        {
            var invocation =
                (InvocationExpressionSyntax)context.Node;

            var memberAccess =
                invocation.Expression as MemberAccessExpressionSyntax;

            if (memberAccess == null)
                return;

            var methodSymbol =
                context.SemanticModel.GetSymbolInfo(
                    invocation,
                    context.CancellationToken).Symbol as IMethodSymbol;

            if (methodSymbol == null)
                return;

            // Detect Task.Wait()
            if (methodSymbol.Name == "Wait" &&
                invocation.ArgumentList.Arguments.Count == 0)
            {
                var receiverType =
                    context.SemanticModel.GetTypeInfo(
                        memberAccess.Expression,
                        context.CancellationToken).Type;

                if (IsTaskType(receiverType))
                {
                    Report(context, memberAccess.Name.GetLocation());
                    return;
                }
            }

            // Detect Task.GetAwaiter().GetResult()
            if (methodSymbol.Name == "GetResult")
            {
                if (IsTaskGetAwaiterResult(memberAccess, context))
                {
                    Report(context, memberAccess.Name.GetLocation());
                }
            }
        }

        private static bool IsTaskType(ITypeSymbol type)
        {
            var namedType = type as INamedTypeSymbol;

            while (namedType != null)
            {
                if (namedType.Name == "Task" &&
                    namedType.ContainingNamespace != null &&
                    namedType.ContainingNamespace.ToDisplayString()
                        == "System.Threading.Tasks")
                {
                    return true;
                }

                namedType = namedType.BaseType;
            }

            return false;
        }

        private static bool IsTaskGetAwaiterResult(
            MemberAccessExpressionSyntax getResultAccess,
            SyntaxNodeAnalysisContext context)
        {
            var getAwaiterInvocation =
                getResultAccess.Expression as InvocationExpressionSyntax;

            if (getAwaiterInvocation == null)
                return false;

            var getAwaiterAccess =
                getAwaiterInvocation.Expression
                    as MemberAccessExpressionSyntax;

            if (getAwaiterAccess == null ||
                getAwaiterAccess.Name.Identifier.ValueText != "GetAwaiter")
            {
                return false;
            }

            var getAwaiterMethod =
                context.SemanticModel.GetSymbolInfo(
                    getAwaiterInvocation,
                    context.CancellationToken).Symbol as IMethodSymbol;

            if (getAwaiterMethod == null ||
                getAwaiterMethod.Name != "GetAwaiter")
            {
                return false;
            }

            return IsTaskType(getAwaiterMethod.ContainingType);
        }

        private static void Report(
            SyntaxNodeAnalysisContext context,
            Location location)
        {
            var diagnostic = Diagnostic.Create(Rule, location);
            context.ReportDiagnostic(diagnostic);
        }
    }
}
