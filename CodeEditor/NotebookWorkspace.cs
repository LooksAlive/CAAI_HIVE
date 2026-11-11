using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CAAI;

internal sealed class NotebookWorkspace : IDisposable
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(NotebookGlobals).Assembly.Location)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    });
    public static IReadOnlyList<MetadataReference> RuntimeReferences => References.Value;

    private readonly Dictionary<NotebookCell, DocumentId> _documents = new();
    private readonly List<ProjectId> _submissions = new();
    private readonly CSharpCompilationOptions _compilationOptions;
    private readonly CSharpParseOptions _parseOptions = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script);

    public AdhocWorkspace Workspace { get; private set; } = new();
    public event Action? SessionChanged;
    public event Action<AdhocWorkspace>? WorkspaceChanged;

    public NotebookWorkspace(IEnumerable<string> imports)
    {
        _compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: imports);
    }

    public DocumentId GetOrCreateDocument(NotebookCell cell)
    {
        if (_documents.TryGetValue(cell, out var existing))
            return existing;

        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = AddScriptProject(Workspace.CurrentSolution, projectId, "Cell", _submissions.LastOrDefault())
            .AddDocument(documentId, "Cell.csx", SourceText.From(cell.Code));
        if (!Workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not create the notebook cell document.");
        _documents.Add(cell, documentId);
        return documentId;
    }

    public void RecordSubmission(string code)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = AddScriptProject(Workspace.CurrentSolution, projectId, "Submission", _submissions.LastOrDefault())
            .AddDocument(documentId, "Submission.csx", SourceText.From(code));
        foreach (var draft in _documents.Values)
            solution = solution.WithProjectReferences(draft.ProjectId, new[] { new ProjectReference(projectId) });
        if (!Workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not update notebook script analysis.");
        _submissions.Add(projectId);
        SessionChanged?.Invoke();
    }

    public void Reset()
    {
        var replacement = new AdhocWorkspace();
        var solution = replacement.CurrentSolution;
        foreach (var (cell, draft) in _documents)
            solution = AddScriptProject(solution, draft.ProjectId, "Cell", null)
                .AddDocument(draft, "Cell.csx", SourceText.From(cell.Code));
        if (!replacement.TryApplyChanges(solution))
        {
            replacement.Dispose();
            throw new InvalidOperationException("Could not reset notebook script analysis.");
        }

        var previous = Workspace;
        Workspace = replacement;
        _submissions.Clear();
        WorkspaceChanged?.Invoke(replacement);
        previous.Dispose();
        SessionChanged?.Invoke();
    }

    public void RemoveCell(NotebookCell cell)
    {
        if (_documents.Remove(cell, out var documentId))
            Workspace.TryApplyChanges(Workspace.CurrentSolution.RemoveProject(documentId.ProjectId));
    }

    private Solution AddScriptProject(Solution solution, ProjectId projectId, string name, ProjectId? previous)
    {
        var project = ProjectInfo.Create(
            projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            compilationOptions: _compilationOptions,
            parseOptions: _parseOptions,
            metadataReferences: References.Value,
            projectReferences: previous is null ? Array.Empty<ProjectReference>() : new[] { new ProjectReference(previous) },
            isSubmission: true,
            hostObjectType: typeof(NotebookGlobals));
        return solution.AddProject(project);
    }

    public void Dispose() => Workspace.Dispose();
}