using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace CAAI;

public class SyntaxTreeExtractor
{
    private static readonly SymbolEqualityComparer SymEq = SymbolEqualityComparer.Default;

    public static void GetAllMethodsOrClassesWithTypeInInputOutput(
        CSharpCompilation compilation,
        List<ITypeSymbol> types)
    {
        var typeSet = new HashSet<ITypeSymbol>(types, SymEq);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // ---------- METHODS ----------
            foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol == null) continue;

                bool match =
                    // return type
                    typeSet.Contains(methodSymbol.ReturnType) ||

                    // parameters
                    methodSymbol.Parameters.Any(p =>
                        typeSet.Contains(p.Type));

                if (match)
                {
                    Console.WriteLine($"METHOD: {methodSymbol.ToDisplayString()}");
                }
            }

            // ---------- CLASSES ----------
            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl);
                if (classSymbol == null) continue;

                bool match = false;

                // base type
                if (classSymbol.BaseType != null &&
                    typeSet.Contains(classSymbol.BaseType))
                {
                    match = true;
                }

                // interfaces
                if (!match && classSymbol.Interfaces.Any(i =>
                        typeSet.Contains(i)))
                {
                    match = true;
                }

                // members
                if (!match)
                {
                    foreach (var member in classSymbol.GetMembers())
                    {
                        switch (member)
                        {
                            case IFieldSymbol field
                                when typeSet.Contains(field.Type):
                            case IPropertySymbol prop
                                when typeSet.Contains(prop.Type):
                                match = true;
                                break;

                            case IMethodSymbol method:
                                if (typeSet.Contains(method.ReturnType) ||
                                    method.Parameters.Any(p =>
                                        typeSet.Contains(p.Type)))
                                {
                                    match = true;
                                }
                                break;
                        }

                        if (match) break;
                    }
                }

                if (match)
                {
                    Console.WriteLine($"CLASS: {classSymbol.ToDisplayString()}");
                }
            }
        }
    }

    static bool UsesType(ITypeSymbol candidate, ITypeSymbol target)
    {
        if (SymEq.Equals(candidate, target))
            return true;

        if (candidate is INamedTypeSymbol named &&
            named.TypeArguments.Any(t => UsesType(t, target)))
            return true;

        return false;
    }
    
	public static Dictionary<INamedTypeSymbol, (INamedTypeSymbol? BaseType, List<INamedTypeSymbol> Interfaces)> BuildTypeGraph(
            CSharpCompilation compilation)
    {
        var graph = new Dictionary<INamedTypeSymbol, (INamedTypeSymbol?, List<INamedTypeSymbol>)>(SymbolEqualityComparer.Default);

        void VisitNamespace(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
                VisitType(type);

            foreach (var child in ns.GetNamespaceMembers())
                VisitNamespace(child);
        }

        void VisitType(INamedTypeSymbol type)
        {
            if (graph.ContainsKey(type))
                return;

            var baseType = type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object
                ? type.BaseType
                : null;

            var interfaces = new List<INamedTypeSymbol>(type.Interfaces);

            graph[type] = (baseType, interfaces);

            // Nested types
            foreach (var nested in type.GetTypeMembers())
                VisitType(nested);
        }

        VisitNamespace(compilation.GlobalNamespace);
        return graph;
    }

    /// <summary>
    /// Print the type graph nicely
    /// </summary>
    public static void PrintTypeGraph(Dictionary<INamedTypeSymbol, (INamedTypeSymbol? BaseType, List<INamedTypeSymbol> Interfaces)> graph)
    {
        foreach (var kvp in graph)
        {
            var type = kvp.Key;
            var (baseType, interfaces) = kvp.Value;

            Console.WriteLine(type.ToDisplayString());

            if (baseType != null)
                Console.WriteLine($"  : {baseType.ToDisplayString()}");

            foreach (var iface in interfaces)
                Console.WriteLine($"  implements {iface.ToDisplayString()}");

            Console.WriteLine();
        }
    }


    private static Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> BuildCallGraph(
        CSharpCompilation compilation)
    {
        var graph = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var methodDeclarations = tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>();

            foreach (var method in methodDeclarations)
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(method);
                if (methodSymbol == null) continue;

                if (!graph.ContainsKey(methodSymbol))
                    graph[methodSymbol] = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

                var invocations = method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var call in invocations)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(call);
                    if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                        graph[methodSymbol].Add(calledMethod);
                }
            }
        }

        return graph;
    }

    public static void PrintCallGraph(Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> graph)
    {
        foreach (var kvp in graph)
        {
            Console.WriteLine(kvp.Key.ToDisplayString());
            foreach (var called in kvp.Value)
                Console.WriteLine($"  calls {called.ToDisplayString()}");
            Console.WriteLine();
        }
    }

    public static IEnumerable<MethodDeclarationSyntax> GetAllMethods(SyntaxTree tree)
    {
        return tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>();
    }

    public static IEnumerable<ISymbol> GetSymbolUsagesInMethod(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        foreach (var node in method.DescendantNodes())
        {
            if (node is not IdentifierNameSyntax id) continue;
            var symbol = ModelExtensions.GetSymbolInfo(semanticModel, id).Symbol;
            if (symbol != null)
                yield return symbol;
        }
    }
    
    public static IEnumerable<IMethodSymbol> GetCallsInMethod(
            MethodDeclarationSyntax method,
            SemanticModel semanticModel)
    {
        foreach (var invocation in method.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            var symbol = ModelExtensions.GetSymbolInfo(semanticModel, invocation)
                .Symbol as IMethodSymbol;

            if (symbol != null)
                yield return symbol;
        }
    }
    
    public static IEnumerable<ISymbol> GetMemberAccessesInMethod(
            MethodDeclarationSyntax method,
            SemanticModel semanticModel)
    {
        foreach (var memberAccess in method.DescendantNodes()
                     .OfType<MemberAccessExpressionSyntax>())
        {
            var symbol = ModelExtensions.GetSymbolInfo(semanticModel, memberAccess).Symbol;
            if (symbol != null)
                yield return symbol;
        }
    }

    public static IEnumerable<ISymbol>
        GetSymbolUsagesOutsideMethods(
            SyntaxTree tree,
            SemanticModel semanticModel)
    {
        var root = tree.GetRoot();

        var nodesOutsideMethods =
            root.DescendantNodes()
                .Where(n => n.Ancestors().OfType<MethodDeclarationSyntax>().Any() == false);

        foreach (var id in nodesOutsideMethods.OfType<IdentifierNameSyntax>())
        {
            var symbol = ModelExtensions.GetSymbolInfo(semanticModel, id).Symbol;
            if (symbol != null)
                yield return symbol;
        }
    }
    
    public static IEnumerable<INamedTypeSymbol>
        GetClassDependencies(
            ClassDeclarationSyntax cls,
            SemanticModel semanticModel)
    {
        foreach (var baseType in cls.BaseList?.Types ?? Enumerable.Empty<BaseTypeSyntax>())
        {
            if (ModelExtensions.GetSymbolInfo(semanticModel, baseType.Type).Symbol is INamedTypeSymbol symbol)
                yield return symbol;
        }

        foreach (var member in cls.Members)
        {
            foreach (var node in member.DescendantNodes())
            {
                var type = ModelExtensions.GetTypeInfo(semanticModel, node).Type as INamedTypeSymbol;
                if (type != null)
                    yield return type;
            }
        }
    }
    
    public static ControlFlowGraph? GetControlFlowGraph(
            MethodDeclarationSyntax method,
            SemanticModel semanticModel)
    {
        return method.Body == null ? null : ControlFlowGraph.Create(method, semanticModel);
    }
    
    public static DataFlowAnalysis? GetDataFlow(
            MethodDeclarationSyntax method,
            SemanticModel semanticModel)
    {
        if (method.Body == null)
            return null;

        return ModelExtensions.AnalyzeDataFlow(semanticModel, method.Body);
    }
    
    


    

    public static Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> BuildDependencyGraph(
    CSharpCompilation compilation)
    {
        var dependencies = new Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

        void VisitNamespace(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
                VisitType(type);

            foreach (var child in ns.GetNamespaceMembers())
                VisitNamespace(child);
        }

        void VisitType(INamedTypeSymbol type)
        {
            if (dependencies.ContainsKey(type))
                return;

            dependencies[type] = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            // Base type
            if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                dependencies[type].Add(type.BaseType);

            // Interfaces
            foreach (var iface in type.Interfaces)
                dependencies[type].Add(iface);

            // Fields and Properties
            foreach (var member in type.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field:
                        if (field.Type is INamedTypeSymbol tField)
                            dependencies[type].Add(tField);
                        break;
                    case IPropertySymbol prop:
                        if (prop.Type is INamedTypeSymbol tProp)
                            dependencies[type].Add(tProp);
                        break;
                    case IMethodSymbol method:
                        // Parameters
                        foreach (var p in method.Parameters)
                            if (p.Type is INamedTypeSymbol tParam)
                                dependencies[type].Add(tParam);
                        // Return type
                        if (method.ReturnType is INamedTypeSymbol tRet)
                            dependencies[type].Add(tRet);
                        break;
                }
            }

            // Nested types
            foreach (var nested in type.GetTypeMembers())
                VisitType(nested);
        }

        VisitNamespace(compilation.GlobalNamespace);
        return dependencies;
    }
    
    public static void PrintDependencyGraph(Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> graph)
    {
        foreach (var kvp in graph)
        {
            Console.WriteLine(kvp.Key.ToDisplayString());
            foreach (var dep in kvp.Value)
                Console.WriteLine($"  depends on {dep.ToDisplayString()}");
            Console.WriteLine();
        }
    }
    
    
    static void PrintTree(SyntaxNode? node, int indent)
    {
        var pad = new string(' ', indent * 2);
        if (node == null) return;
        Console.WriteLine($"{pad}{node.Kind()}");

        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
                PrintTree(child.AsNode(), indent + 1);
            else
            {
                var token = child.AsToken();
                Console.WriteLine($"{pad}  TOKEN: {token.Kind()} -> '{token.Text}'");
            }
        }
    }

    static void PrintOperation(IOperation op, int indent)
    {
        string pad = new string(' ', indent * 2);
        Console.WriteLine($"{pad}- {op.Kind} ({op.GetType().Name})");

        // Print type info if available
        if (op.Type != null)
        {
            Console.WriteLine($"{pad}  Type: {op.Type}");
        }

        // Print children recursively
        foreach (var child in op.ChildOperations)
        {
            PrintOperation(child, indent + 1);
        }
    }
    
    private static readonly HashSet<string> VisitedTypes = [];
    
    private static void AnalyzeCompilationUnit(SyntaxNode? root)
    {
        if (root == null) return;

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var cls in classes)
        {
            AnalyzeClass(cls);
        }
    }
    
    private static void AnalyzeClass(ClassDeclarationSyntax cls)
    {
        var className = cls.Identifier.Text;
        if (!VisitedTypes.Add(className)) return; // avoid cycles
        Console.WriteLine("Class: " + className);

        // Base type
        if (cls.BaseList != null)
        {
            foreach (var baseType in cls.BaseList.Types)
            {
                Console.WriteLine("  Base: " + baseType.Type.ToString());
                // Optionally, recursively resolve base type if source available
            }
        }

        // Fields
        foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                Console.WriteLine($"  Field: {variable.Identifier.Text} : {field.Declaration.Type}");
                // optionally track type for recursive analysis
            }
        }

        // Properties
        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>())
        {
            Console.WriteLine($"  Property: {prop.Identifier.Text} : {prop.Type}");
        }

        // Methods
        foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
        {
            Console.WriteLine($"  Method: {method.Identifier.Text} : {method.ReturnType}");

            foreach (var param in method.ParameterList.Parameters)
            {
                Console.WriteLine($"    Param: {param.Identifier.Text} : {param.Type}");
            }

            // Optionally, you can analyze the method body to find dataflow later
            // method.Body or method.ExpressionBody
        }
    }
}

public static class SymbolExtensions
{
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(this INamespaceSymbol namespaceSymbol)
    {
        // Get types in the current namespace
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;
        }

        // Recursively look into nested namespaces
        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in childNamespace.GetAllTypes())
            {
                yield return type;
            }
        }
    }
}