using Microsoft.CodeAnalysis.Emit;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CAAI;

public static class RoslynCompiler
{
    public static (byte[]? dll, byte[]? pdb) Compile(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);

        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));

        var compilation = CSharpCompilation.Create(
            "HotReloadAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var dllStream = new MemoryStream();
        using var pdbStream = new MemoryStream();

        var result = compilation.Emit(dllStream, pdbStream);

        if (result.Success) return (dllStream.ToArray(), pdbStream.ToArray());
        foreach (var diag in result.Diagnostics)
            Console.WriteLine(diag);

        return (null, null);

    }
}

public class IDEHotReloadService
{
    private EmitBaseline _baseline = null!;
    private Assembly _assembly = null!;
	
	public IDEHotReloadService(Assembly assembly, EmitBaseline baseline)
	{
		Initialize(assembly, baseline);
	}

    public void Initialize(Assembly assembly, EmitBaseline baseline)
    {
        _assembly = assembly;
        _baseline = baseline;
    }

    public void ApplyUpdate(Compilation compilation)
    {
        var edits = new[]
        {
            new SemanticEdit(
                SemanticEditKind.Update,
                null, // old symbol
                null  // new symbol (simplified here)
            )
        };

        using var mdStream = new MemoryStream();
        using var ilStream = new MemoryStream();
        using var pdbStream = new MemoryStream();

        var diff1 = compilation.EmitDifference(
            _baseline,
            edits,
            null!,
            mdStream,
            ilStream,
            pdbStream);

        if (!diff1.Success)
        {
            Console.WriteLine("Hot reload failed:");
            foreach (var d in diff1.Diagnostics)
                Console.WriteLine(d);
            return;
        }

        System.Reflection.Metadata.MetadataUpdater.ApplyUpdate(
            _assembly,
            mdStream.ToArray(),
            ilStream.ToArray(),
            pdbStream.ToArray());

        Console.WriteLine("Hot reload applied.");
    }
}