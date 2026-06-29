using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace CAAI;
public sealed class PatternEngine(SyntaxTree syntaxTree)
{
    public SyntaxTree SyntaxTree { get; } = syntaxTree;

    private readonly HashSet<object> visited =
        new(new ReferenceEqualityComparer());

    private readonly Dictionary<object, int> freqTable =
        new(new ReferenceEqualityComparer());

    private readonly Dictionary<object, string> freqTableDisplay =
        new(new ReferenceEqualityComparer());

    private readonly Dictionary<(object Parent, object Child), int> coOccurrence =
        new();

    private readonly Dictionary<object, List<object>> enclosures =
        new(new ReferenceEqualityComparer());


    private static bool IsLeaf(object? obj)
    {
        return obj is null
               || obj is string
               || obj.GetType().IsPrimitive
               || obj is decimal
               || obj is DateTime
               || obj is Guid;
    }
    
    public void Traverse(
    object? obj,
    object? parent,
    string path,
    Stack<object>? pathStack = null)
{
    if (obj == null)
        return;

    pathStack ??= new Stack<object>();

    // -------------------------
    // Leaf detection
    // -------------------------
    if (IsLeaf(obj))
        return;

    // -------------------------
    // Cycle detection (path-based)
    // -------------------------
    if (pathStack.Contains(obj))
        return;

    pathStack.Push(obj);

    // -------------------------
    // RECORD: frequency
    // -------------------------
    freqTable.TryGetValue(obj, out var freq);
    freqTable[obj] = freq + 1;

    freqTableDisplay[obj] = obj.GetType().Name;

    // -------------------------
    // RECORD: co-occurrence
    // -------------------------
    if (parent != null)
    {
        var key = (parent, obj);
        coOccurrence.TryGetValue(key, out var count);
        coOccurrence[key] = count + 1;
    }

    // -------------------------
    // RECORD: enclosure root
    // -------------------------
    if (!enclosures.ContainsKey(obj))
        enclosures[obj] = new List<object>();

    var type = obj.GetType();

    // -------------------------
    // ImmutableArray<T>
    // -------------------------
    if (type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
    {
        var isDefaultProp = type.GetProperty("IsDefault");
        if (isDefaultProp != null &&
            (bool)isDefaultProp.GetValue(obj)!)
        {
            pathStack.Pop();
            return;
        }

        foreach (var item in (IEnumerable)obj)
        {
            if (item == null)
                continue;

            if (!IsLeaf(item))
                enclosures[obj].Add(item);

            Traverse(item, obj, path + "[]", pathStack);
        }

        pathStack.Pop();
        return;
    }

    // -------------------------
    // IEnumerable (not string)
    // -------------------------
    if (obj is IEnumerable enumerable and not string)
    {
        foreach (var item in enumerable)
        {
            if (item == null)
                continue;

            if (!IsLeaf(item))
                enclosures[obj].Add(item);

            Traverse(item, obj, path + "[]", pathStack);
        }

        pathStack.Pop();
        return;
    }

    // -------------------------
    // Object properties
    // -------------------------
    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!prop.CanRead)
            continue;

        if (prop.GetIndexParameters().Length > 0)
            continue;

        object? value;
        try
        {
            value = prop.GetValue(obj);
        }
        catch
        {
            continue; // Roslyn throws here sometimes
        }

        if (value == null)
            continue;

        if (!IsLeaf(value))
            enclosures[obj].Add(value);

        Traverse(value, obj, $"{path}.{prop.Name}", pathStack);
    }

    pathStack.Pop();
}

    
    public void PrintPatterns(
    int topN = 20,
    int minFrequency = 2,
    int minCoOccurrence = 2)
    {
        Console.WriteLine("\n================ PATTERN SUMMARY ================\n");

        // -------------------------------------------------
        // 1. Frequency patterns
        // -------------------------------------------------
        Console.WriteLine("=== Frequency Patterns ===");

        foreach (var kv in freqTable
                 .OrderByDescending(kv => kv.Value)
                 .Take(topN)
                 .Where(kv => kv.Value >= minFrequency))
        {
            Console.WriteLine(
                $"[{kv.Value,4}x] {freqTableDisplay[kv.Key]}");
        }

        Console.WriteLine();

        // -------------------------------------------------
        // 2. Co-occurrence patterns (parent -> child)
        // -------------------------------------------------
        Console.WriteLine("=== Co-occurrence Patterns (A → B) ===");

        foreach (var kv in coOccurrence
                 .OrderByDescending(kv => kv.Value)
                 .Take(topN)
                 .Where(kv => kv.Value >= minCoOccurrence))
        {
            var parentName = freqTableDisplay.TryGetValue(kv.Key.Parent, out var p)
                ? p
                : kv.Key.Parent.GetType().Name;

            var childName = freqTableDisplay.TryGetValue(kv.Key.Child, out var c)
                ? c
                : kv.Key.Child.GetType().Name;

            Console.WriteLine(
                $"[{kv.Value,4}x] {parentName}  →  {childName}");
        }

        Console.WriteLine();

        // -------------------------------------------------
        // 3. Enclosure patterns
        // -------------------------------------------------
        Console.WriteLine("=== Enclosure Patterns ===");

        foreach (var kv in enclosures
                 .Where(kv => kv.Value.Count > 0)
                 .OrderByDescending(kv => kv.Value.Count)
                 .Take(topN))
        {
            var containerName = freqTableDisplay.TryGetValue(kv.Key, out var c)
                ? c
                : kv.Key.GetType().Name;

            Console.WriteLine(
                $"{containerName} encloses {kv.Value.Count} objects:");

            foreach (var child in kv.Value
                         .Select(o => freqTableDisplay.TryGetValue(o, out var d)
                             ? d
                             : o.GetType().Name)
                         .Distinct()
                         .Take(10))
            {
                Console.WriteLine($"   - {child}");
            }

            Console.WriteLine();
        }

        // -------------------------------------------------
        // 4. Structural hubs (high fan-out)
        // -------------------------------------------------
        Console.WriteLine("=== Structural Hubs (High Fan-out) ===");

        foreach (var kv in enclosures
                 .OrderByDescending(kv => kv.Value.Distinct().Count())
                 .Take(topN))
        {
            var name = freqTableDisplay.TryGetValue(kv.Key, out var d)
                ? d
                : kv.Key.GetType().Name;

            Console.WriteLine(
                $"{name} → {kv.Value.Distinct().Count()} distinct children");
        }

        Console.WriteLine("\n=================================================\n");
    }

}

/// Helper for tracking visited objects by reference, not equality
public class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
