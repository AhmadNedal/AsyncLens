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

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(Rule); }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        // task.Result  (Task<T>)
        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var memberAccess = (MemberAccessExpressionSyntax)context.Node;

            if (memberAccess.Name.Identifier.ValueText != "Result")
                return;

            var property = context.SemanticModel
                .GetSymbolInfo(memberAccess, context.CancellationToken).Symbol as IPropertySymbol;

            if (property == null || property.Name != "Result")
                return;

            if (!IsSystemType(property.ContainingType, "System.Threading.Tasks", "Task", 1))
                return;

            Report(context, memberAccess.Name.GetLocation());
        }

        // task.Wait(...)  and  task.GetAwaiter().GetResult()
        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
            if (memberAccess == null)
                return;

            string name = memberAccess.Name.Identifier.ValueText;
            if (name != "Wait" && name != "GetResult")
                return;

            var method = context.SemanticModel
                .GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;

            if (method == null)
                return;

            if (name == "Wait")
            {
                // Wait is declared on System.Threading.Tasks.Task (also for Task<T> receivers).
                if (method.Name == "Wait" &&
                    IsSystemType(method.ContainingType, "System.Threading.Tasks", "Task", 0))
                {
                    Report(context, memberAccess.Name.GetLocation());
                }
            }
            else if (method.Name == "GetResult")
            {
                // Only the real TaskAwaiter / TaskAwaiter<T>, so custom awaiters are not flagged.
                var type = method.ContainingType;
                if (IsSystemType(type, "System.Runtime.CompilerServices", "TaskAwaiter", 0) ||
                    IsSystemType(type, "System.Runtime.CompilerServices", "TaskAwaiter", 1))
                {
                    Report(context, memberAccess.Name.GetLocation());
                }
            }
        }

        private static bool IsSystemType(INamedTypeSymbol type, string ns, string name, int arity)
        {
            if (type == null || type.Name != name || type.Arity != arity)
                return false;

            return type.ContainingNamespace?.ToDisplayString() == ns;
        }

        private static void Report(SyntaxNodeAnalysisContext context, Location location)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location));
        }
    }
}