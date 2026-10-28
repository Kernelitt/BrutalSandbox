using System.Diagnostics;

namespace TestSandbox.Framework
{
    public static class Profiler
    {
        private static readonly Dictionary<string, Stopwatch> _watchers = [];
        private static readonly Dictionary<string, double> _results = [];
        private static long _startMemory;

        public static void BeginSample(string name)
        {
            if (!_watchers.TryGetValue(name, out var sw))
            {
                sw = new Stopwatch();
                _watchers[name] = sw;
            }
            sw.Restart();
        }

        public static void EndSample(string name)
        {
            if (_watchers.TryGetValue(name, out var sw))
            {
                sw.Stop();
                _results[name] = sw.Elapsed.TotalMilliseconds;
            }
        }

        public static double GetSampleTime(string name)
        {
            return _results.TryGetValue(name, out double time) ? time : 0.0;
        }

        // Замер памяти на кадр
        public static void BeginMemorySample() => _startMemory = GC.GetTotalMemory(false);
        public static long EndMemorySample() => GC.GetTotalMemory(false) - _startMemory;

        public static void PrintResults()
        {
            Console.WriteLine("=== FRAME PERFORMANCE PROFILER ===");
            foreach (var kvp in _results)
            {
                Console.WriteLine($"[{kvp.Key}]: {kvp.Value:F3} ms");
            }
        }

        public static string[] GetResults()
        {
            string[] results = [];

            _ = results.Append("=== FRAME PERFORMANCE PROFILER ===");

            foreach (var kvp in _results)
            {
                _ = results.Append($"[{kvp.Key}]: {kvp.Value:F3} ms");
            }

            return results;
        }

        public static Dictionary<string, double> GetRawResults() => _results;
    }
}