using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;

namespace CAAI;

public static class EditorRefactoringService
{
    private static readonly Lazy<ImmutableArray<CodeRefactoringProvider>> BundledProviders = new(LoadProviders);
    private static readonly object RegistrationLock = new();
    private static ImmutableArray<CodeRefactoringProvider> _providers = [new ExtractInterfaceRefactoringProvider()];

    public static void Register(CodeRefactoringProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (RegistrationLock)
            if (!_providers.Contains(provider)) _providers = _providers.Add(provider);
    }

    public static async Task<IReadOnlyList<CodeAction>> GetActionsAsync(
        Document document, TextSpan selection, CancellationToken cancellationToken = default)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (selection.End > text.Length) return [];
        ImmutableArray<CodeRefactoringProvider> providers;
        lock (RegistrationLock) providers = _providers;
        var actions = new List<CodeAction>();
        foreach (var provider in providers.AddRange(BundledProviders.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, selection,
                    action => actions.Add(action), cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { Debug.WriteLine($"Refactoring {provider.GetType().Name}: {exception.Message}"); }
        }
        return actions.SelectMany(Flatten).DistinctBy(action => (action.Title, action.EquivalenceKey)).ToArray();
    }

    public static async Task<Solution> RenameAsync(Document document, int position, string newName,
        CancellationToken cancellationToken = default)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (position < 0 || position > text.Length) throw new ArgumentOutOfRangeException(nameof(position));
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Place the caret on a source symbol to rename.");
        return await RenameSymbolAsync(document.Project.Solution, symbol, newName, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Solution> RenameSymbolAsync(Solution solution, ISymbol symbol, string newName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!SyntaxFacts.IsValidIdentifier(newName)) throw new ArgumentException("Enter a valid C# identifier.", nameof(newName));
        if (!symbol.Locations.Any(location => location.IsInSource) || symbol.IsImplicitlyDeclared)
            throw new InvalidOperationException("Only explicitly declared source symbols can be renamed.");
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol,
            new SymbolRenameOptions(RenameOverloads: false, RenameInStrings: false, RenameInComments: false, RenameFile: false),
            newName, cancellationToken).ConfigureAwait(false);
        foreach (var change in renamed.GetChanges(solution).GetProjectChanges())
        {
            var before = await change.OldProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var after = await change.NewProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (before == null || after == null) throw new InvalidOperationException("Could not validate the renamed code.");
            var oldErrors = before.GetDiagnostics(cancellationToken).Where(item => item.Severity == DiagnosticSeverity.Error)
                .GroupBy(item => (item.Id, Message: item.GetMessage())).ToDictionary(group => group.Key, group => group.Count());
            var newErrors = after.GetDiagnostics(cancellationToken).Where(item => item.Severity == DiagnosticSeverity.Error)
                .GroupBy(item => (item.Id, Message: item.GetMessage()));
            if (newErrors.Any(group => group.Count() > oldErrors.GetValueOrDefault(group.Key)))
                throw new InvalidOperationException("The rename introduces compiler errors or a naming conflict. No changes were applied.");
        }
        return renamed;
    }

    public static async Task ApplyTextChangesAsync(Workspace workspace, Solution original, Solution updated,
        CancellationToken cancellationToken = default)
    {
        if (original.Id != updated.Id || workspace.CurrentSolution.Id != original.Id)
            throw new InvalidOperationException("The workspace changed. Invoke the action again.");
        var changes = updated.GetChanges(original);
        if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
            throw new InvalidOperationException("Actions that add or remove projects are not supported.");
        var current = workspace.CurrentSolution;
        var result = current;
        foreach (var change in changes.GetProjectChanges())
        {
            if (change.GetAddedDocuments().Any() || change.GetRemovedDocuments().Any() ||
                change.GetAddedAdditionalDocuments().Any() || change.GetRemovedAdditionalDocuments().Any() || change.GetChangedAdditionalDocuments().Any() ||
                change.GetAddedAnalyzerConfigDocuments().Any() || change.GetRemovedAnalyzerConfigDocuments().Any() || change.GetChangedAnalyzerConfigDocuments().Any() ||
                change.GetAddedMetadataReferences().Any() || change.GetRemovedMetadataReferences().Any() ||
                change.GetAddedProjectReferences().Any() || change.GetRemovedProjectReferences().Any() ||
                change.GetAddedAnalyzerReferences().Any() || change.GetRemovedAnalyzerReferences().Any() ||
                change.OldProject.Name != change.NewProject.Name || change.OldProject.AssemblyName != change.NewProject.AssemblyName ||
                change.OldProject.FilePath != change.NewProject.FilePath || change.OldProject.OutputFilePath != change.NewProject.OutputFilePath ||
                !Equals(change.OldProject.CompilationOptions, change.NewProject.CompilationOptions) ||
                !Equals(change.OldProject.ParseOptions, change.NewProject.ParseOptions))
                throw new InvalidOperationException("This action creates/removes files or changes project configuration and cannot be applied here.");
            foreach (var id in change.GetChangedDocuments())
            {
                var before = original.GetDocument(id)!;
                var after = updated.GetDocument(id)!;
                var live = current.GetDocument(id) ?? throw new InvalidOperationException("An affected document was closed. Invoke the action again.");
                if (before.Name != after.Name || before.FilePath != after.FilePath || before.SourceCodeKind != after.SourceCodeKind ||
                    !before.Folders.SequenceEqual(after.Folders))
                    throw new InvalidOperationException("Actions that rename or move files are not supported.");
                if (!(await before.GetTextAsync(cancellationToken).ConfigureAwait(false))
                    .ContentEquals(await live.GetTextAsync(cancellationToken).ConfigureAwait(false)))
                    throw new InvalidOperationException("An affected document changed. Invoke the action again.");
                result = result.WithDocumentText(id, await after.GetTextAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(current, workspace.CurrentSolution) || !workspace.TryApplyChanges(result))
            throw new InvalidOperationException("The workspace changed or rejected the action. No changes were applied.");
    }

    private static IEnumerable<CodeAction> Flatten(CodeAction action) => action.NestedActions.IsEmpty
        ? [action] : action.NestedActions.SelectMany(Flatten);

    private static ImmutableArray<CodeRefactoringProvider> LoadProviders()
    {
        var providers = ImmutableArray.CreateBuilder<CodeRefactoringProvider>();
        foreach (var name in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
        {
            try
            {
                foreach (var type in Assembly.Load(name).GetTypes().Where(type => !type.IsAbstract && typeof(CodeRefactoringProvider).IsAssignableFrom(type)))
                {
                    var export = type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>();
                    if (export == null || !export.Languages.Contains(LanguageNames.CSharp)) continue;
                    try
                    {
                        if (Activator.CreateInstance(type, nonPublic: true) is CodeRefactoringProvider provider) providers.Add(provider);
                    }
                    catch (Exception exception) { Debug.WriteLine($"Refactoring unavailable ({type.Name}): {exception.Message}"); }
                }
            }
            catch (Exception exception) { Debug.WriteLine($"Refactoring discovery ({name}): {exception.Message}"); }
        }
        return providers.ToImmutable();
    }
}

public sealed class ExtractInterfaceRefactoringProvider : CodeRefactoringProvider
{
    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null || root.FullSpan.IsEmpty) return;
        var declaration = root.FindToken(Math.Min(context.Span.Start, root.FullSpan.End - 1)).Parent?
            .AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (declaration == null || declaration.TypeParameterList != null || declaration.Parent is TypeDeclarationSyntax ||
            declaration.Modifiers.Any(token => token.IsKind(SyntaxKind.PartialKeyword) || token.IsKind(SyntaxKind.StaticKeyword) || token.IsKind(SyntaxKind.FileKeyword))) return;
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (model?.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol symbol) return;
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in declaration.Members)
        {
            if (model.GetDeclaredSymbol(member, context.CancellationToken) is not ISymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false }) continue;
            IEnumerable<SyntaxNode> signature = member switch
            {
                MethodDeclarationSyntax method => new SyntaxNode[] { method.ReturnType }
                    .Concat(method.ParameterList.Parameters.Where(parameter => parameter.Type != null).Select(parameter => (SyntaxNode)parameter.Type!))
                    .Concat(method.ParameterList.Parameters.Where(parameter => parameter.Default != null).Select(parameter => (SyntaxNode)parameter.Default!.Value))
                    .Concat(method.ConstraintClauses.SelectMany(clause => clause.Constraints.OfType<TypeConstraintSyntax>()).Select(constraint => (SyntaxNode)constraint.Type)),
                PropertyDeclarationSyntax property => [property.Type],
                _ => []
            };
            var expandedNodes = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var node in signature)
                expandedNodes[node] = await Simplifier.ExpandAsync(node, context.Document, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            var expanded = member.ReplaceNodes(expandedNodes.Keys, (original, _) => expandedNodes[original]);
            switch (expanded)
            {
                case MethodDeclarationSyntax method when method.ExplicitInterfaceSpecifier == null && !method.Modifiers.Any(token => token.IsKind(SyntaxKind.OverrideKeyword)):
                    members.Add(method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                        .WithModifiers(ContractModifiers(method.Modifiers)).WithAttributeLists(default).WithoutTrivia());
                    break;
                case PropertyDeclarationSyntax property when property.ExplicitInterfaceSpecifier == null:
                    var accessors = property.AccessorList?.Accessors.Where(accessor =>
                        accessor.Modifiers.Count == 0 || accessor.Modifiers.Any(token => token.IsKind(SyntaxKind.PublicKeyword)))
                        .Select(accessor => accessor.WithBody(null).WithExpressionBody(null).WithModifiers(default)
                            .WithAttributeLists(default).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))).ToArray()
                        ?? [SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))];
                    if (accessors.Length > 0)
                        members.Add(property.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
                            .WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(default)
                            .WithModifiers(ContractModifiers(property.Modifiers)).WithAttributeLists(default).WithoutTrivia());
                    break;
            }
        }
        if (members.Count == 0) return;
        var name = "I" + symbol.Name;
        if (symbol.ContainingNamespace.GetTypeMembers(name).Length > 0) return;
        var contract = SyntaxFactory.InterfaceDeclaration(name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(symbol.DeclaredAccessibility == Accessibility.Public ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword))
                .AddRange(ContractModifiers(declaration.Modifiers)))
            .WithMembers(SyntaxFactory.List(members)).WithAdditionalAnnotations(Formatter.Annotation);
        var implementation = declaration.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(name)))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var replacementRoot = root.ReplaceNode(declaration, new SyntaxNode[] { contract, implementation });
        context.RegisterRefactoring(CodeAction.Create($"Extract interface '{name}' in this file", async cancellationToken =>
        {
            var changed = context.Document.WithSyntaxRoot(replacementRoot);
            return await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        }, "CAAI.ExtractInterface"));
    }

    private static SyntaxTokenList ContractModifiers(SyntaxTokenList modifiers) =>
        SyntaxFactory.TokenList(modifiers.Where(token => token.IsKind(SyntaxKind.UnsafeKeyword)));
}