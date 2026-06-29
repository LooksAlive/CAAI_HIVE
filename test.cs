using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyApp.Example
{
    /// <summary>
    /// A sample interface demonstrating a service contract.
    /// </summary>
    public interface IService<T>
    {
        Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default);
        T Transform(T input);
    }

    /// <summary>
    /// A simple record type with positional parameters.
    /// </summary>
    public record Point(int X, int Y)
    {
        public static Point Origin => new(0, 0);
        public double DistanceFromOrigin() => Math.Sqrt(X * X + Y * Y);
        public void Deconstruct(out int x, out int y)
        {
            x = X;
            y = Y;
        }
    }

    /// <summary>
    /// Generic repository implementing IService for any T.
    /// </summary>
    public class Repository<T> : IService<T>
    {
        public readonly List<T> _store = new();

        public Repository() { }

        public Repository(IEnumerable<T> initial)
        {
            _store.AddRange(initial);
        }

        public virtual Task<IEnumerable<T>> GetAllAsync(CancellationToken ct = default)
        {
            // Simulate async I/O
            return Task.FromResult<IEnumerable<T>>(_store.ToArray());
        }

        public virtual T Transform(T input)
        {
            // Default behavior: return the same value
            return input;
        }

        public void Add(T item) => _store.Add(item);
        public bool Remove(T item) => _store.Remove(item);

        // Indexer example
        public T this[int index]
        {
            get => _store[index];
            set => _store[index] = value;
        }
    }

    /// <summary>
    /// A specialized repository for points that overrides Transform.
    /// </summary>
    public class PointRepository : Repository<Point>
    {
        public PointRepository() : base()
        {
            Add(new Point(1, 2));
            Add(new Point(3, 4));
        }

        public override Point Transform(Point input)
        {
            // Move point by (1,1)
            return input with { X = input.X + 1, Y = input.Y + 1 };
        }

        // Example of an iterator method
        public IEnumerable<Point> FilterByDistance(double minDistance)
        {
            foreach (var p in _store)
            {
                if (p.DistanceFromOrigin() >= minDistance)
                    yield return p;
            }
        }
    }

    /// <summary>
    /// Static utility class with extension methods.
    /// </summary>
    public static class Extensions
    {
        // Extension on IEnumerable<T>
        public static IEnumerable<T> TakeEvery<T>(this IEnumerable<T> seq, int step)
        {
            if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
            int i = 0;
            foreach (var s in seq)
            {
                if (i % step == 0) yield return s;
                i++;
            }
        }

        // Generic swap helper (uses tuple deconstruction)
        public static void Swap<T>(ref T a, ref T b)
        {
            (a, b) = (b, a);
        }
    }

    /// <summary>
    /// Example of event and delegate usage.
    /// </summary>
    public class Notifier
    {
        // custom delegate
        public delegate void NotifyHandler(object? sender, string message);

        public event NotifyHandler? OnNotify;

        public void Notify(string message)
        {
            OnNotify?.Invoke(this, message);
        }
    }

    /// <summary>
    /// Example consumer showing async/await, try/catch and pattern matching.
    /// </summary>
    public static class Consumer
    {
        public static async Task RunDemoAsync()
        {
            var repo = new PointRepository();
            var pts = await repo.GetAllAsync();

            // LINQ + extension use
            var sampled = pts.TakeEvery(2).ToList();

            foreach (var p in sampled)
            {
                Console.WriteLine($"Sample point: {p}");
            }

            // pattern matching with tuple
            foreach (var p in pts)
            {
                var (x, y) = p;
                var quadrant = (x, y) switch
                {
                    (> 0, > 0) => "I",
                    (< 0, > 0) => "II",
                    (< 0, < 0) => "III",
                    (> 0, < 0) => "IV",
                    _ => "Axis"
                };
                Console.WriteLine($"Point {p} is in quadrant {quadrant}");
            }

            // async with cancellation
            using var cts = new CancellationTokenSource(1000);
            try
            {
                var all = await repo.GetAllAsync(cts.Token);
                Console.WriteLine($"Got {all.Count()} points");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Operation cancelled");
            }
        }
    }

    /// <summary>
    /// Demonstrates local functions, overloaded operators, and exceptions.
    /// </summary>
    public class MathHelper
    {
        public static int Sum(params int[] values) => values.Sum();

        public static double AverageOrDefault(IEnumerable<int> seq)
        {
            if (seq == null) throw new ArgumentNullException(nameof(seq));

            if (!seq.Any()) return 0.0;
            return seq.Average();
        }

        // Local function example inside a method
        public static int ComputeWithLocal(int a, int b)
        {
            int Multiply(int x, int y) => x * y;
            return Multiply(a + 1, b + 2);
        }
    }

    /// <summary>
    /// Small struct with operator overloads.
    /// </summary>
    public struct Vec
    {
        public readonly double X;
        public readonly double Y;

        public Vec(double x, double y) { X = x; Y = y; }

        public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
        public static Vec operator *(Vec v, double s) => new Vec(v.X * s, v.Y * s);

        public override string ToString() => $"Vec({X},{Y})";
    }

    /// <summary>
    /// Program entrypoint that ties things together.
    /// </summary>
    public static class ProgramEntry
    {
        public static async Task Main1(string[] args)
        {
            Console.WriteLine("Demo starting...");
            await Consumer.RunDemoAsync();

            var notifier = new Notifier();
            notifier.OnNotify += (s, msg) => Console.WriteLine($"NOTIFY: {msg}");
            notifier.Notify("Hello subscribers!");

            // Demonstrate Swap
            int a = 1, b = 2;
            Extensions.Swap(ref a, ref b);
            Console.WriteLine($"Swapped: a={a}, b={b}");

            // Use Vec
            var v = new Vec(1, 2) + new Vec(3, 4) * 2;
            Console.WriteLine(v);
        }
    }
}
