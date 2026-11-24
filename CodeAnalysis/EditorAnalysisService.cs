using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace CAAI;

public static class EditorAnalysisService
{
    private static readonly DiagnosticAnalyzer DefaultAnalyzer = new EditorQualityAnalyzer();
    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> BuiltInAnalyzers = new(LoadBuiltInAnalyzers);
    private static readonly Lazy<ImmutableArray<CodeFixProvider>> BuiltInFixes = new(LoadBuiltInFixes);
    private static readonly object RegistrationLock = new();
    private static ImmutableArray<DiagnosticAnalyzer> _analyzers = [DefaultAnalyzer];
    private static ImmutableArray<CodeFixProvider> _fixes = [new EditorQualityCodeFixProvider()];

    public static void Register(DiagnosticAnalyzer analyzer, CodeFixProvider? codeFix = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        lock (RegistrationLock)
        {
            if (!_analyzers.Contains(analyzer)) _analyzers = _analyzers.Add(analyzer);
            if (codeFix != null && !_fixes.Contains(codeFix)) _fixes = _fixes.Add(codeFix);
        }
    }

    public static Solution AddEditorConfigs(Solution solution, ProjectId projectId, string folderPath)
    {
        var project = solution.GetProject(projectId) ?? throw new ArgumentException("Project not found.", nameof(projectId));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = project.AnalyzerConfigDocuments.Select(document => document.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = project.Documents.Where(document => document.FilePath != null)
            .Select(document => Path.GetDirectoryName(document.FilePath!)!)
            .Append(Path.GetFullPath(folderPath));
        foreach (var folder in folders)
        {
            for (var directory = new DirectoryInfo(folder); directory != null && visited.Add(directory.FullName); directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, ".editorconfig");
                if (!File.Exists(path) || !existing.Add(path)) continue;
                try
                {
                    solution = solution.AddAnalyzerConfigDocument(DocumentId.CreateNewId(projectId), ".editorconfig",
                        SourceText.From(File.ReadAllText(path)), filePath: path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not load {path}: {exception.Message}");
                }
            }
        }
        return solution;
    }

    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        Document document, CancellationToken cancellationToken = default)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (compilation == null || tree == null) return [];
        ImmutableArray<DiagnosticAnalyzer> analyzers;
        lock (RegistrationLock) analyzers = _analyzers;
        analyzers = analyzers.AddRange(BuiltInAnalyzers.Value).AddRange(document.Project.AnalyzerReferences
            .SelectMany(reference => reference.GetAnalyzers(document.Project.Language))).Distinct().ToImmutableArray();
        var options = new CompilationWithAnalyzersOptions(document.Project.AnalyzerOptions,
            (exception, analyzer, diagnostic) => Debug.WriteLine($"Analyzer {analyzer.GetType().Name}: {exception.Message}"),
            concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
        var runner = compilation.WithAnalyzers(analyzers, options);
        var compilerDiagnostics = compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken);
        var analyzerDiagnostics = await runner.GetAnalyzerSemanticDiagnosticsAsync(
            compilation.GetSemanticModel(tree), null, cancellationToken).ConfigureAwait(false);
        var syntaxDiagnostics = await runner.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken).ConfigureAwait(false);
        return compilerDiagnostics.Concat(analyzerDiagnostics).Concat(syntaxDiagnostics)
            .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity != DiagnosticSeverity.Hidden &&
                diagnostic.Location.SourceTree == tree)
            .Distinct().OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(diagnostic => diagnostic.Id).ToArray();
    }

    public static async Task<IReadOnlyList<CodeAction>> GetFixesAsync(
        Document document, int caretOffset, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var line = text.Lines.GetLineFromPosition(Math.Clamp(caretOffset, 0, text.Length));
        var diagnostics = await GetDiagnosticsAsync(document, cancellationToken).ConfigureAwait(false);
        ImmutableArray<CodeFixProvider> fixes;
        lock (RegistrationLock) fixes = _fixes;
        fixes = fixes.AddRange(BuiltInFixes.Value);
        var actions = new List<CodeAction>();
        foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.Location.SourceSpan.IntersectsWith(line.Span)))
        {
            foreach (var provider in fixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id)) continue;
                    var context = new CodeFixContext(document, diagnostic,
                        (action, _) => actions.Add(action), cancellationToken);
                    await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { Debug.WriteLine($"Code fix {provider.GetType().Name}: {exception.Message}"); }
            }
        }
        return actions.SelectMany(Flatten).DistinctBy(action => (action.Title, action.EquivalenceKey)).ToArray();
    }

    private static IEnumerable<CodeAction> Flatten(CodeAction action) => action.NestedActions.IsEmpty
        ? [action] : action.NestedActions.SelectMany(Flatten);

    private static ImmutableArray<DiagnosticAnalyzer> LoadBuiltInAnalyzers()
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var name in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
        {
            try
            {
                foreach (var type in Assembly.Load(name).GetTypes().Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type)))
                {
                    var export = type.GetCustomAttribute<DiagnosticAnalyzerAttribute>();
                    if (export == null || !export.Languages.Contains(LanguageNames.CSharp)) continue;
                    try
                    {
                        if (Activator.CreateInstance(type, nonPublic: true) is DiagnosticAnalyzer analyzer) analyzers.Add(analyzer);
                    }
                    catch (Exception exception) { Debug.WriteLine($"Analyzer unavailable ({type.Name}): {exception.Message}"); }
                }
            }
            catch (Exception exception) { Debug.WriteLine($"Analyzer discovery ({name}): {exception.Message}"); }
        }
        return analyzers.ToImmutable();
    }

    private static ImmutableArray<CodeFixProvider> LoadBuiltInFixes()
    {
        var providers = ImmutableArray.CreateBuilder<CodeFixProvider>();
        foreach (var assemblyName in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
        {
            try
            {
                var assembly = Assembly.Load(assemblyName);
                foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type)))
                {
                    var export = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                    if (export == null || !export.Languages.Contains(LanguageNames.CSharp)) continue;
                    try
                    {
                        if (Activator.CreateInstance(type, nonPublic: true) is CodeFixProvider provider) providers.Add(provider);
                    }
                    catch (Exception exception) { Debug.WriteLine($"Code fix unavailable ({type.Name}): {exception.Message}"); }
                }
            }
            catch (Exception exception) { Debug.WriteLine($"Code fix discovery ({assemblyName}): {exception.Message}"); }
        }
        return providers.ToImmutable();
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1001", Justification = "Registered directly by the editor, not loaded by the compiler.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("MicrosoftCodeAnalysisReleaseTracking", "RS2008", Justification = "In-process editor rules are not a distributed analyzer package.")]
public sealed class EditorQualityAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor BooleanComparison = new("CAAI1001", "Redundant boolean comparison",
        "Simplify comparison with a boolean constant", "Style", DiagnosticSeverity.Info, true);
    private static readonly DiagnosticDescriptor Rethrow = new("CAAI1002", "Preserve the exception stack trace",
        "Use 'throw;' to preserve the original exception stack trace", "Reliability", DiagnosticSeverity.Warning, true);
    private static readonly DiagnosticDescriptor EmptyCatch = new("CAAI1003", "Empty catch block",
        "This catch block silently discards an exception", "Reliability", DiagnosticSeverity.Warning, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [BooleanComparison, Rethrow, EmptyCatch];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeComparison, OperationKind.Binary);
        context.RegisterSyntaxNodeAction(AnalyzeThrow, SyntaxKind.ThrowStatement);
        context.RegisterSyntaxNodeAction(nodeContext =>
        {
            var clause = (CatchClauseSyntax)nodeContext.Node;
            if (clause.Block.Statements.Count == 0)
                nodeContext.ReportDiagnostic(Diagnostic.Create(EmptyCatch, clause.CatchKeyword.GetLocation()));
        }, SyntaxKind.CatchClause);
    }

    private static void AnalyzeComparison(OperationAnalysisContext context)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorMethod != null || binary.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals) ||
            binary.LeftOperand.Type?.SpecialType != SpecialType.System_Boolean ||
            binary.RightOperand.Type?.SpecialType != SpecialType.System_Boolean) return;
        if (binary.LeftOperand is IConversionOperation { OperatorMethod: not null } ||
            binary.RightOperand is IConversionOperation { OperatorMethod: not null }) return;
        if (binary.LeftOperand.ConstantValue.HasValue == binary.RightOperand.ConstantValue.HasValue) return;
        context.ReportDiagnostic(Diagnostic.Create(BooleanComparison, binary.Syntax.GetLocation()));
    }

    private static void AnalyzeThrow(SyntaxNodeAnalysisContext context)
    {
        var statement = (ThrowStatementSyntax)context.Node;
        if (statement.Expression is not IdentifierNameSyntax identifier) return;
        var boundary = statement.Ancestors().FirstOrDefault(node => node is CatchClauseSyntax or
            AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        if (boundary is not CatchClauseSyntax { Declaration: { } declaration }) return;
        var caught = context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken);
        if (caught != null && SymbolEqualityComparer.Default.Equals(caught,
            context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol))
        {
            var dataFlow = context.SemanticModel.AnalyzeDataFlow(((CatchClauseSyntax)boundary).Block);
            if (dataFlow is { Succeeded: true } && !dataFlow.WrittenInside.Contains(caught, SymbolEqualityComparer.Default))
                context.ReportDiagnostic(Diagnostic.Create(Rethrow, statement.GetLocation()));
        }
    }
}

public sealed class EditorQualityCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ["CAAI1001", "CAAI1002"];
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null || model == null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            SyntaxNode? replacement = null;
            var title = "";
            if (diagnostic.Id == "CAAI1002" && node is ThrowStatementSyntax statement)
            {
                replacement = statement.WithExpression(null).WithThrowKeyword(
                    statement.ThrowKeyword.WithTrailingTrivia(SyntaxFactory.ElasticMarker));
                title = "Preserve stack trace with 'throw;'";
            }
            else if (diagnostic.Id == "CAAI1001" && node is BinaryExpressionSyntax binary)
            {
                var left = model.GetConstantValue(binary.Left, context.CancellationToken);
                var operand = left.HasValue ? binary.Right : binary.Left;
                var constant = left.HasValue ? left : model.GetConstantValue(binary.Right, context.CancellationToken);
                if (constant.Value is not bool boolean) continue;
                var positive = boolean == binary.IsKind(SyntaxKind.EqualsExpression);
                var expression = SyntaxFactory.ParenthesizedExpression(operand.WithoutTrivia());
                replacement = (positive ? (ExpressionSyntax)expression :
                    SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, expression)).WithTriviaFrom(binary);
                title = "Simplify boolean comparison";
            }
            if (replacement == null) continue;
            var updated = context.Document.WithSyntaxRoot(root.ReplaceNode(node, replacement));
            context.RegisterCodeFix(CodeAction.Create(title, _ => Task.FromResult(updated), diagnostic.Id), diagnostic);
        }
    }
}