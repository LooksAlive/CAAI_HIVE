using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace CAAI;

internal static class QuickActions
{
    public static async Task<IReadOnlyList<CodeAction>> GetActionsAsync(
        Document document, int caretOffset, CancellationToken cancellationToken, bool includeDocumentActions = true)
    {
        var actions = new List<CodeAction>();
        if (includeDocumentActions)
        {
            var text = await document.GetTextAsync(cancellationToken);
            var formattedDocument = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
            var formattedText = await formattedDocument.GetTextAsync(cancellationToken);
            if (!text.ContentEquals(formattedText))
            {
                actions.Add(CodeAction.Create(
                    "Format document",
                    _ => Task.FromResult(formattedDocument),
                    equivalenceKey: "FormatDocument"));
            }
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null || root.FullSpan.IsEmpty)
            return actions;

        var token = root.FindToken(Math.Clamp(caretOffset, 0, root.FullSpan.End - 1));
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel is null)
            return actions;

        var usingDirective = token.Parent?.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault();
        if (usingDirective is not null && usingDirective.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
        {
            var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
            if (!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
                diagnostics.Any(diagnostic => diagnostic.Id == "CS8019" &&
                    diagnostic.Location.SourceSpan.IntersectsWith(usingDirective.Span)))
            {
                var updatedRoot = root.RemoveNode(usingDirective, SyntaxRemoveOptions.KeepExteriorTrivia);
                if (updatedRoot is not null)
                {
                    actions.Add(CodeAction.Create(
                        "Remove unused using",
                        _ => Task.FromResult(document.WithSyntaxRoot(updatedRoot)),
                        equivalenceKey: "RemoveUnusedUsing"));
                }
            }
        }

        var declaration = token.Parent?.AncestorsAndSelf().OfType<VariableDeclarationSyntax>().FirstOrDefault();
        if (declaration?.Parent is not LocalDeclarationStatementSyntax ||
            declaration.Type is not IdentifierNameSyntax identifier || identifier.Identifier.Text != "var")
            return actions;

        if (semanticModel.GetAliasInfo(identifier, cancellationToken) is not null)
            return actions;

        var type = semanticModel.GetTypeInfo(identifier, cancellationToken).Type;
        if (type is null || !CanWriteType(type))
            return actions;

        var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var action = CodeAction.Create(
            $"Use explicit type '{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'",
            async token =>
            {
                var text = await document.GetTextAsync(token);
                return document.WithText(text.WithChanges(new TextChange(identifier.Span, typeName)));
            },
            equivalenceKey: "UseExplicitType");
        actions.Add(action);
        return actions;
    }

    private static bool CanWriteType(ITypeSymbol type) =>
        !type.IsAnonymousType && type.TypeKind is not (TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer) &&
        (type is not IArrayTypeSymbol array || CanWriteType(array.ElementType)) &&
        (type is not INamedTypeSymbol named || named.TypeArguments.All(CanWriteType));
}