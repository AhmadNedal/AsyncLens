using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncLens
{
    public static class AsyncLensHelpers
    {
        public const string TasksNs = "System.Threading.Tasks";
        public const string CompilerServicesNs = "System.Runtime.CompilerServices";

        public static bool IsType(INamedTypeSymbol type, string ns, string name, int arity)
        {
            if (type == null || type.Name != name || type.Arity != arity)
                return false;

            return type.ContainingNamespace?.ToDisplayString() == ns;
        }

        public static bool IsTaskOrTaskOfT(INamedTypeSymbol type)
        {
            return IsType(type, TasksNs, "Task", 0) || IsType(type, TasksNs, "Task", 1);
        }

        // Task<T>.Result or ValueTask<T>.Result
        public static bool IsTaskResult(IPropertySymbol property)
        {
            return property != null &&
                   property.Name == "Result" &&
                   (IsType(property.ContainingType, TasksNs, "Task", 1) ||
                    IsType(property.ContainingType, TasksNs, "ValueTask", 1));
        }

        // instance Task.Wait(...)
        public static bool IsTaskWait(IMethodSymbol method)
        {
            return method != null &&
                   method.Name == "Wait" &&
                   !method.IsStatic &&
                   IsType(method.ContainingType, TasksNs, "Task", 0);
        }

        // static Task.WaitAll / Task.WaitAny
        public static bool IsTaskWaitAllOrAny(IMethodSymbol method)
        {
            return method != null &&
                   method.IsStatic &&
                   (method.Name == "WaitAll" || method.Name == "WaitAny") &&
                   IsType(method.ContainingType, TasksNs, "Task", 0);
        }

        // GetResult() on the real runtime awaiters (custom awaiters are not matched).
        public static bool IsAwaiterGetResult(IMethodSymbol method)
        {
            if (method == null || method.Name != "GetResult" || method.IsStatic)
                return false;

            var type = method.ContainingType;
            if (IsType(type, CompilerServicesNs, "TaskAwaiter", 0) ||
                IsType(type, CompilerServicesNs, "TaskAwaiter", 1) ||
                IsType(type, CompilerServicesNs, "ValueTaskAwaiter", 0) ||
                IsType(type, CompilerServicesNs, "ValueTaskAwaiter", 1))
                return true;

            if (type != null && type.Arity == 0 &&
                (type.Name == "ConfiguredTaskAwaiter" || type.Name == "ConfiguredValueTaskAwaiter"))
            {
                var outer = type.ContainingType;
                return outer != null &&
                       outer.ContainingNamespace?.ToDisplayString() == CompilerServicesNs &&
                       (outer.Name == "ConfiguredTaskAwaitable" || outer.Name == "ConfiguredValueTaskAwaitable");
            }

            return false;
        }

        // GetAwaiter() declared on Task / ValueTask / their ConfigureAwait results.
        public static bool IsAwaitableGetAwaiter(IMethodSymbol method)
        {
            if (method == null || method.Name != "GetAwaiter")
                return false;

            var type = method.ContainingType;
            return IsType(type, TasksNs, "Task", 0) ||
                   IsType(type, TasksNs, "Task", 1) ||
                   IsType(type, TasksNs, "ValueTask", 0) ||
                   IsType(type, TasksNs, "ValueTask", 1) ||
                   IsType(type, CompilerServicesNs, "ConfiguredTaskAwaitable", 0) ||
                   IsType(type, CompilerServicesNs, "ConfiguredTaskAwaitable", 1) ||
                   IsType(type, CompilerServicesNs, "ConfiguredValueTaskAwaitable", 0) ||
                   IsType(type, CompilerServicesNs, "ConfiguredValueTaskAwaitable", 1);
        }

        public static bool IsThreadSleep(IMethodSymbol method)
        {
            return method != null &&
                   method.Name == "Sleep" &&
                   method.IsStatic &&
                   method.Parameters.Length == 1 &&
                   IsType(method.ContainingType, "System.Threading", "Thread", 0);
        }

        // Strips parentheses and ConfigureAwait(...) so we reach the underlying task expression.
        public static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression != null)
            {
                var parenthesized = expression as ParenthesizedExpressionSyntax;
                if (parenthesized != null)
                {
                    expression = parenthesized.Expression;
                    continue;
                }

                var invocation = expression as InvocationExpressionSyntax;
                var access = invocation == null ? null : invocation.Expression as MemberAccessExpressionSyntax;
                if (access != null && access.Name.Identifier.ValueText == "ConfigureAwait")
                {
                    expression = access.Expression;
                    continue;
                }

                break;
            }

            return expression;
        }

        // Nearest enclosing function (method / local function / lambda) is 'async'.
        public static bool IsInsideAsyncFunction(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                var method = ancestor as MethodDeclarationSyntax;
                if (method != null)
                    return method.Modifiers.Any(SyntaxKind.AsyncKeyword);

                var local = ancestor as LocalFunctionStatementSyntax;
                if (local != null)
                    return local.Modifiers.Any(SyntaxKind.AsyncKeyword);

                var anon = ancestor as AnonymousFunctionExpressionSyntax;
                if (anon != null)
                    return anon.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);

                if (ancestor is BaseMethodDeclarationSyntax || ancestor is AccessorDeclarationSyntax)
                    return false;
            }

            return false;
        }

        public static bool ImplementsInterfaceMember(IMethodSymbol symbol)
        {
            var type = symbol.ContainingType;
            if (type == null)
                return false;

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetMembers())
                {
                    var impl = type.FindImplementationForInterfaceMember(member);
                    if (SymbolEqualityComparer.Default.Equals(impl, symbol))
                        return true;
                }
            }

            return false;
        }
    }
}