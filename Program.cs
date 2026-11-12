using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using Avalonia;

namespace CAAI
{
    public class Program
    {
        private static void PrintMemory(string label)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long managed = GC.GetTotalMemory(true);
            long privateMem = Process.GetCurrentProcess().PrivateMemorySize64;

            Console.WriteLine($"{label}");
            Console.WriteLine($"  Managed heap: {managed / 1024 / 1024} MB");
            Console.WriteLine($"  Private mem : {privateMem / 1024 / 1024} MB");
        }

        private void initRefs()
        {
            var references2 = new List<MetadataReference>();
            // Core runtime assemblies
            var assemblies = new[]
            {
                typeof(object).Assembly,                  // System.Private.CoreLib
                typeof(Console).Assembly,                 // System.Console
                typeof(Enumerable).Assembly,              // System.Linq
                typeof(List<>).Assembly,                  // System.Collections
                typeof(Uri).Assembly,                     // System.Runtime
                typeof(Task).Assembly,                    // System.Threading.Tasks
                typeof(System.Text.StringBuilder).Assembly,
                typeof(System.IO.File).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
                typeof(System.Diagnostics.Debug).Assembly
            };

            foreach (var asm in assemblies)
            {
                references2.Add(
                    MetadataReference.CreateFromFile(asm.Location)
                );
            }
        }
        
        // Avalonia configuration
        private static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();

        static void Main(string[] args)
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            return;
            
            
            /*
            var syntaxDiagnostics = tree.GetDiagnostics();
            foreach (var diag in syntaxDiagnostics)
                Console.WriteLine($"[SYNTAX] {diag}");

            var semanticDiagnostics = compilation.GetDiagnostics();
            foreach (var diag in semanticDiagnostics)
                Console.WriteLine($"[SEMANTIC] {diag}");

            var modelDiagnostics = model.GetDiagnostics();
            foreach (var diag in modelDiagnostics)
                Console.WriteLine($"[MODEL] {diag}");
            */

            /*
            // --- Optional: analyzers ---
            var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
                // add custom analyzers here
            );
            if (!analyzers.IsEmpty)
            {
                var analyzerCompilation = compilation.WithAnalyzers(analyzers);
                var analyzerDiagnostics = await analyzerCompilation.GetAllDiagnosticsAsync();

                Console.WriteLine("\n=== Analyzer Diagnostics ===");
                foreach (var d in analyzerDiagnostics)
                    Console.WriteLine("[ANALYZER] " + d);
            }
            */
        }
    }
}
