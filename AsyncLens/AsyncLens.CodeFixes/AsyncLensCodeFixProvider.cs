using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncLens
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AsyncLensCodeFixProvider)), Shared]
    public class AsyncLensCodeFixProvider : CodeFixProvider
    {
        private const string MakeAsyncTitle = "Make method async and await the task";
        private const string SleepTitle = "Replace Thread.Sleep with await Task.Delay";

        private enum ReturnKind { None, Void, Task, TaskOfT, Other }

        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get
            {
                return ImmutableArray.Create(
                    AsyncLensAnalyzer.DiagnosticId,
                    AsyncLensAnalyzer.SleepDiagnosticId);
            }
        }

        // Fix All is intentionally disabled: the built-in BatchFixer can apply the
        // "make method async" fix twice to the same method when it has several diagnostics.
        public sealed override FixAllProvider GetFixAllProvider()
        {
            return null;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
                return;

            var diagnostic = context.Diagnostics[0];
            var token = root.FindToken(diagnostic.Location.SourceSpan.Start);

            var access = token.Parent?.Parent as MemberAccessExpressionSyntax;
            if (access == null || access.Name.Identifier != token)
                return;

            if (diagnostic.Id == AsyncLensAnalyzer.SleepDiagnosticId)
            {
                RegisterSleepFix(context, access, diagnostic);
                return;
            }

            ExpressionSyntax target, task;
            if (!TryGetFixTarget(access, out target, out task))
                return;

            if (!IsAwaitAllowedAt(target))
                return;

            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel == null ||
                !IsConfirmedBlockingCall(access, semanticModel, context.CancellationToken))
                return;

            // Case 1: already inside an async function (or top-level statements) -> simple replacement.
            if (AsyncLensHelpers.IsInsideAsyncFunction(target) || IsInTopLevelStatement(target))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: CodeFixResources.CodeFixTitle,
                        createChangedDocument: ct => ReplaceWithAwaitAsync(context.Document, target, task, ct),
                        equivalenceKey: nameof(CodeFixResources.CodeFixTitle)),
                    diagnostic);
                return;
            }

            // Case 2: directly inside a non-async method -> offer to make the method async.
            var method = GetDirectEnclosingMethod(target);
            if (method == null)
                return;

            ReturnKind kind;
            if (!CanMakeMethodAsync(method, semanticModel, context.CancellationToken, out kind))
                return;

            // Changing the return type (void / T -> Task) is only safe when nobody can call the method.
            if (kind == ReturnKind.Void || kind == ReturnKind.Other)
            {
                var symbol = semanticModel.GetDeclaredSymbol(method, context.CancellationToken);
                if (symbol == null || IsExternallyVisible(symbol))
                    return;

                if (await HasReferencesAsync(context.Document, symbol, context.CancellationToken).ConfigureAwait(false))
                    return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: MakeAsyncTitle,
                    createChangedDocument: ct => MakeAsyncAndAwaitAsync(context.Document, target, task, method, kind, ct),
                    equivalenceKey: "AsyncLens.MakeMethodAsync"),
                diagnostic);
        }

        // ---------------------------------------------------------------- Thread.Sleep fix (ASYNC002)

        private static void RegisterSleepFix(
            CodeFixContext context, MemberAccessExpressionSyntax access, Diagnostic diagnostic)
        {
            var invocation = access.Parent as InvocationExpressionSyntax;
            if (invocation == null ||
                invocation.Expression != access ||
                !(invocation.Parent is ExpressionStatementSyntax))
                return;

            if (!IsAwaitAllowedAt(invocation))
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: SleepTitle,
                    createChangedDocument: ct => ReplaceSleepAsync(context.Document, invocation, ct),
                    equivalenceKey: "AsyncLens.ReplaceSleep"),
                diagnostic);
        }

        private static async Task<Document> ReplaceSleepAsync(
            Document document, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var arguments = invocation.ArgumentList.WithoutTrailingTrivia().ToFullString();
            var delay = SyntaxFactory.ParseExpression("System.Threading.Tasks.Task.Delay" + arguments)
                .WithAdditionalAnnotations(Simplifier.Annotation);

            ExpressionSyntax awaited = SyntaxFactory.AwaitExpression(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                delay);

            var newRoot = root.ReplaceNode(invocation, awaited.WithTriviaFrom(invocation));
            var newDocument = document.WithSyntaxRoot(newRoot);

            return await Simplifier.ReduceAsync(
                newDocument, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- target detection (ASYNC001)

        private static bool TryGetFixTarget(
            MemberAccessExpressionSyntax access, out ExpressionSyntax target, out ExpressionSyntax task)
        {
            target = null;
            task = null;

            switch (access.Name.Identifier.ValueText)
            {
                case "Result":
                    target = access;
                    task = access.Expression;
                    return true;

                case "Wait":
                    {
                        // Only the parameterless Wait() used as a statement (other overloads return bool).
                        var inv = access.Parent as InvocationExpressionSyntax;
                        if (inv == null || inv.Expression != access ||
                            inv.ArgumentList.Arguments.Count != 0 ||
                            !(inv.Parent is ExpressionStatementSyntax))
                            return false;

                        target = inv;
                        task = access.Expression;
                        return true;
                    }

                case "GetResult":
                    {
                        var inv = access.Parent as InvocationExpressionSyntax;
                        if (inv == null || inv.Expression != access || inv.ArgumentList.Arguments.Count != 0)
                            return false;

                        var getAwaiterCall = access.Expression as InvocationExpressionSyntax;
                        var getAwaiterAccess = getAwaiterCall?.Expression as MemberAccessExpressionSyntax;
                        if (getAwaiterAccess == null ||
                            getAwaiterAccess.Name.Identifier.ValueText != "GetAwaiter" ||
                            getAwaiterCall.ArgumentList.Arguments.Count != 0)
                            return false;

                        target = inv;
                        task = getAwaiterAccess.Expression;
                        return true;
                    }
            }

            // WaitAll / WaitAny and anything else: report only, no automatic fix.
            return false;
        }

        private static bool IsConfirmedBlockingCall(
            MemberAccessExpressionSyntax access, SemanticModel model, CancellationToken ct)
        {
            switch (access.Name.Identifier.ValueText)
            {
                case "Result":
                    return AsyncLensHelpers.IsTaskResult(model.GetSymbolInfo(access, ct).Symbol as IPropertySymbol);

                case "Wait":
                    {
                        var inv = access.Parent as InvocationExpressionSyntax;
                        var method = inv == null ? null : model.GetSymbolInfo(inv, ct).Symbol as IMethodSymbol;
                        return AsyncLensHelpers.IsTaskWait(method) && method.Parameters.Length == 0;
                    }

                case "GetResult":
                    {
                        var inv = access.Parent as InvocationExpressionSyntax;
                        var getResult = inv == null ? null : model.GetSymbolInfo(inv, ct).Symbol as IMethodSymbol;
                        if (!AsyncLensHelpers.IsAwaiterGetResult(getResult))
                            return false;

                        // The awaiter must come from Task / ValueTask (or their ConfigureAwait), not a custom extension.
                        var getAwaiterCall = access.Expression as InvocationExpressionSyntax;
                        var getAwaiter = getAwaiterCall == null
                            ? null
                            : model.GetSymbolInfo(getAwaiterCall, ct).Symbol as IMethodSymbol;
                        return AsyncLensHelpers.IsAwaitableGetAwaiter(getAwaiter);
                    }
            }

            return false;
        }

        // ---------------------------------------------------------------- context checks

        private static bool IsInTopLevelStatement(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is MethodDeclarationSyntax ||
                    ancestor is LocalFunctionStatementSyntax ||
                    ancestor is AnonymousFunctionExpressionSyntax ||
                    ancestor is BaseMethodDeclarationSyntax ||
                    ancestor is AccessorDeclarationSyntax)
                    return false;

                if (ancestor is GlobalStatementSyntax)
                    return true;
            }

            return false;
        }

        // The method that directly contains the node (null if a lambda/local function/ctor/accessor is closer).
        private static MethodDeclarationSyntax GetDirectEnclosingMethod(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                var method = ancestor as MethodDeclarationSyntax;
                if (method != null)
                    return method;

                if (ancestor is LocalFunctionStatementSyntax ||
                    ancestor is AnonymousFunctionExpressionSyntax ||
                    ancestor is BaseMethodDeclarationSyntax ||
                    ancestor is AccessorDeclarationSyntax)
                    return null;
            }

            return null;
        }

        private static bool IsAwaitAllowedAt(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is LockStatementSyntax ||       // CS1996
                    ancestor is UnsafeStatementSyntax ||     // CS4004
                    ancestor is CatchFilterClauseSyntax ||   // CS7094
                    ancestor is QueryExpressionSyntax)
                    return false;

                var method = ancestor as MethodDeclarationSyntax;
                if (method != null)
                    return !method.Modifiers.Any(SyntaxKind.UnsafeKeyword);

                var local = ancestor as LocalFunctionStatementSyntax;
                if (local != null)
                    return !local.Modifiers.Any(SyntaxKind.UnsafeKeyword);

                if (ancestor is AnonymousFunctionExpressionSyntax)
                    return true;
            }

            return true;
        }

        private static bool CanMakeMethodAsync(
            MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct, out ReturnKind kind)
        {
            kind = ReturnKind.None;

            if (method.Modifiers.Any(SyntaxKind.PartialKeyword) ||
                method.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
                method.Modifiers.Any(SyntaxKind.ExternKeyword) ||
                method.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                return false;

            if (method.Body == null && method.ExpressionBody == null)
                return false;

            var symbol = model.GetDeclaredSymbol(method, ct);
            if (symbol == null || symbol.ReturnsByRef || symbol.ReturnsByRefReadonly)
                return false;

            if (symbol.ExplicitInterfaceImplementations.Length > 0)
                return false;

            // async methods cannot have ref/out/in parameters or pointer/ref-struct parameters.
            if (symbol.Parameters.Any(p => p.RefKind != RefKind.None ||
                                           p.Type.IsRefLikeType ||
                                           p.Type.TypeKind == TypeKind.Pointer))
                return false;

            // Iterators (yield) cannot be turned into async methods this way.
            if (OwnDescendants<YieldStatementSyntax>(method).Any())
                return false;

            var returnType = symbol.ReturnType;
            if (symbol.ReturnsVoid)
            {
                kind = ReturnKind.Void;
            }
            else if (returnType.TypeKind == TypeKind.Pointer || returnType.IsRefLikeType)
            {
                return false;
            }
            else
            {
                var named = returnType as INamedTypeSymbol;
                if (AsyncLensHelpers.IsType(named, AsyncLensHelpers.TasksNs, "Task", 0))
                    kind = ReturnKind.Task;
                else if (AsyncLensHelpers.IsType(named, AsyncLensHelpers.TasksNs, "Task", 1))
                    kind = ReturnKind.TaskOfT;
                else if (named != null && named.Name == "ValueTask")
                    return false; // would become Task<ValueTask<T>>: not supported
                else
                    kind = ReturnKind.Other;
            }

            // Changing the return type would break overrides / interface implementations.
            if ((kind == ReturnKind.Void || kind == ReturnKind.Other) &&
                (symbol.IsOverride || AsyncLensHelpers.ImplementsInterfaceMember(symbol)))
                return false;

            // 'return null;' in a Task method would become a NullReferenceException after 'await'.
            if (kind == ReturnKind.Task || kind == ReturnKind.TaskOfT)
            {
                if (OwnDescendants<ReturnStatementSyntax>(method)
                    .Any(r => r.Expression != null && r.Expression.IsKind(SyntaxKind.NullLiteralExpression)))
                    return false;
            }

            return true;
        }

        // public/protected member of public/protected types (InternalsVisibleTo is not considered).
        private static bool IsExternallyVisible(ISymbol symbol)
        {
            for (var s = symbol; s != null && s.Kind != SymbolKind.Namespace; s = s.ContainingSymbol)
            {
                var accessibility = s.DeclaredAccessibility;
                if (accessibility != Accessibility.Public &&
                    accessibility != Accessibility.Protected &&
                    accessibility != Accessibility.ProtectedOrInternal)
                    return false;
            }

            return true;
        }

        private static async Task<bool> HasReferencesAsync(
            Document document, ISymbol symbol, CancellationToken cancellationToken)
        {
            var references = await SymbolFinder.FindReferencesAsync(
                symbol, document.Project.Solution, cancellationToken).ConfigureAwait(false);

            return references.Any(r => r.Locations.Any());
        }

        // Descendants that belong to this method itself (not to nested lambdas / local functions).
        private static IEnumerable<T> OwnDescendants<T>(SyntaxNode root) where T : SyntaxNode
        {
            return root
                .DescendantNodes(n => !(n is AnonymousFunctionExpressionSyntax) && !(n is LocalFunctionStatementSyntax))
                .OfType<T>();
        }

        // ---------------------------------------------------------------- fix 1: already async

        private static async Task<Document> ReplaceWithAwaitAsync(
            Document document, ExpressionSyntax target, ExpressionSyntax task, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var newRoot = root.ReplaceNode(target, CreateAwaitFor(target, task));
            return document.WithSyntaxRoot(newRoot);
        }

        private static ExpressionSyntax CreateAwaitFor(ExpressionSyntax target, ExpressionSyntax task)
        {
            ExpressionSyntax awaited = CreateAwait(task);

            // (await x).Foo / (await x)[0] / (await x)() etc. need parentheses.
            if (NeedsParentheses(target))
                awaited = SyntaxFactory.ParenthesizedExpression(awaited);

            return awaited.WithTriviaFrom(target);
        }

        private static ExpressionSyntax CreateAwait(ExpressionSyntax operandSource)
        {
            ExpressionSyntax operand = operandSource.WithoutTrivia();
            if (!IsSimpleOperand(operand))
                operand = SyntaxFactory.ParenthesizedExpression(operand);

            return SyntaxFactory.AwaitExpression(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                operand);
        }

        private static bool IsSimpleOperand(ExpressionSyntax e)
        {
            return e is IdentifierNameSyntax
                || e is GenericNameSyntax
                || e is MemberAccessExpressionSyntax
                || e is InvocationExpressionSyntax
                || e is ElementAccessExpressionSyntax
                || e is ParenthesizedExpressionSyntax
                || e is ObjectCreationExpressionSyntax
                || e is ThisExpressionSyntax;
        }

        private static bool NeedsParentheses(ExpressionSyntax target)
        {
            var parent = target.Parent;
            if (parent is MemberAccessExpressionSyntax ||
                parent is ElementAccessExpressionSyntax ||
                parent is ConditionalAccessExpressionSyntax ||
                parent is PostfixUnaryExpressionSyntax)
                return true;

            var inv = parent as InvocationExpressionSyntax;
            return inv != null && inv.Expression == target;
        }

        // ---------------------------------------------------------------- fix 2: make method async

        private static async Task<Document> MakeAsyncAndAwaitAsync(
            Document document,
            ExpressionSyntax target,
            ExpressionSyntax task,
            MethodDeclarationSyntax method,
            ReturnKind kind,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // 1) replace the blocking expression inside the method
            var newMethod = method.ReplaceNode(target, CreateAwaitFor(target, task));

            // 2) returns / expression body: Task-returning methods must now 'await' what they returned
            if (kind == ReturnKind.Task || kind == ReturnKind.TaskOfT)
            {
                if (newMethod.Body != null)
                {
                    var body = newMethod.Body;
                    var newBody = (BlockSyntax)new ReturnRewriter(kind, body).Visit(body);
                    newMethod = newMethod.WithBody(newBody);
                }
                else if (newMethod.ExpressionBody != null)
                {
                    var expr = newMethod.ExpressionBody.Expression;
                    var awaited = CreateAwait(expr).WithTriviaFrom(expr);
                    newMethod = newMethod.WithExpressionBody(newMethod.ExpressionBody.WithExpression(awaited));
                }
            }

            // 3) return type (void -> Task, T -> Task<T>)
            var returnType = newMethod.ReturnType;
            TypeSyntax newReturnType = returnType;
            if (kind == ReturnKind.Void)
            {
                newReturnType = SyntaxFactory.ParseTypeName("System.Threading.Tasks.Task");
            }
            else if (kind == ReturnKind.Other)
            {
                newReturnType = SyntaxFactory.ParseTypeName(
                    "System.Threading.Tasks.Task<" + returnType.WithoutTrivia().ToString() + ">");
            }

            if (kind == ReturnKind.Void || kind == ReturnKind.Other)
            {
                newReturnType = newReturnType
                    .WithTriviaFrom(returnType)
                    .WithAdditionalAnnotations(Simplifier.Annotation);
            }

            // 4) add the 'async' modifier (last modifier, right before the return type)
            var asyncToken = SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);

            if (newMethod.Modifiers.Count == 0)
            {
                asyncToken = asyncToken.WithLeadingTrivia(newReturnType.GetLeadingTrivia());
                newReturnType = newReturnType.WithLeadingTrivia(SyntaxFactory.TriviaList());
            }

            newMethod = newMethod
                .WithModifiers(newMethod.Modifiers.Add(asyncToken))
                .WithReturnType(newReturnType);

            var newRoot = root.ReplaceNode(method, newMethod.WithAdditionalAnnotations(Formatter.Annotation));
            var newDocument = document.WithSyntaxRoot(newRoot);

            newDocument = await Simplifier.ReduceAsync(
                newDocument, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);

            return newDocument;
        }

        // Rewrites the method's own 'return' statements (does not enter lambdas / local functions).
        private sealed class ReturnRewriter : CSharpSyntaxRewriter
        {
            private readonly ReturnKind _kind;
            private readonly BlockSyntax _body;

            public ReturnRewriter(ReturnKind kind, BlockSyntax body)
            {
                _kind = kind;
                _body = body;
            }

            public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
            {
                if (node.Expression == null)
                    return node;

                var awaited = CreateAwait(node.Expression).WithTriviaFrom(node.Expression);

                // Task<T>:  return x;  ->  return await x;
                if (_kind == ReturnKind.TaskOfT)
                    return node.WithExpression(awaited);

                // Task:  return x;  ->  await x;   (at the end of the method)
                //        return x;  ->  { await x; return; }   (anywhere else)
                var statement = SyntaxFactory.ExpressionStatement(awaited)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());

                bool isLast = node.Parent == _body && _body.Statements.Last() == node;
                if (isLast)
                    return statement;

                return SyntaxFactory.Block(
                        statement.WithoutLeadingTrivia(),
                        SyntaxFactory.ReturnStatement())
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithAdditionalAnnotations(Formatter.Annotation);
            }

            public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { return node; }
            public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { return node; }
            public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { return node; }
            public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) { return node; }
        }
    }
}