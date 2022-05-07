using Mono.Options;
using Prometheus;
using Prometheus.DotNetRuntime;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Consumer;

public sealed class Program
{
    static void Main(string[] args)
    {
        new Program().Execute(args);
    }

    private void Execute(string[] args)
    {
        if (!ParseArguments(args))
        {
            Environment.ExitCode = -1;
            return;
        }

        Console.CancelKeyPress += OnControlC;

        SetupMetrics();

        var waitForMemoryConsumerToExit = StartConsumingMemory();
        var waitForCpuConsumersToExit = StartConsumingCpu();

        waitForCpuConsumersToExit();
        waitForMemoryConsumerToExit();

        Console.WriteLine("All done.");
    }

    private void OnControlC(object? sender, ConsoleCancelEventArgs e)
    {
        _cts.Cancel();
        e.Cancel = true; // We have handled it.
    }

    private void SetupMetrics()
    {
        var metricServer = new KestrelMetricServer(_metricsPort);
        // We never stop it, just shuts down at end of process.
        metricServer.Start();

        DotNetRuntimeStatsBuilder.Default().StartCollecting();

        Console.WriteLine($"Publishing metrics on http://+:{_metricsPort}/metrics");
    }

    private Action StartConsumingCpu()
    {
        var threads = new List<Thread>();

        for (var i = 0; i < _cpuCores; i++)
        {
            var thread = new Thread(ConsumeOneCpuCore)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();

            threads.Add(thread);
        }

        void WaitForExit()
        {
            foreach (var thread in threads)
                thread.Join();
        }

        return WaitForExit;
    }

    private void ConsumeOneCpuCore()
    {
        Console.WriteLine("Consuming 1 CPU core worth of CPU time.");

        var inputBuffer = RandomNumberGenerator.GetBytes(1 * 1024 * 1024);
        var outputBuffer = new byte[512 / 8];

        while (!_cancel.IsCancellationRequested)
            _ = SHA512.HashData(inputBuffer, outputBuffer);
    }

    private Action StartConsumingMemory()
    {
        if (_memoryGigabytes == null)
            return delegate { };

        // We split the memory up into 1 MB chunks just because .NET might have a harder time allocating one giant slab.
        var megabytes = _memoryGigabytes.Value * 1024;

        var buffers = new List<byte[]>(megabytes);

        for (var i = 0; i < megabytes; i++)
        {
            if (_cancel.IsCancellationRequested)
                return delegate { };

            if (i % 1024 == 0)
            {
                var progress = i * 1.0 / megabytes;
                Console.WriteLine($"Allocating memory... {progress:P0}");
            }

            buffers.Add(RandomNumberGenerator.GetBytes(1 * 1024 * 1024));

            // Don't get over excited with all the RNGing.
            Thread.Yield();
        }

        Console.WriteLine("Memory successfully allocated.");

        // Allocation and first write success! Now start keeping that memory alive.
        void KeepItAlive()
        {
            // Touch each 4K page.
            const int pageSize = 4096;
            var pages = 1 * 1024 * 1024 / pageSize;

            while (!_cancel.IsCancellationRequested)
            {
                foreach (var buffer in buffers)
                {
                    for (var page = 0; page < pages; page++)
                    {
                        unchecked
                        {
                            buffer[page * pageSize]++;
                        }
                    }
                }

                // That's enough, now wait a bit to avoid the memory consumer also becoming a CPU consumer.
                Thread.Sleep(1000);
            }
        }

        var keepItAliveThread = new Thread(KeepItAlive)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };

        keepItAliveThread.Start();

        return () => keepItAliveThread.Join();
    }

    private Program()
    {
        _cancel = _cts.Token;
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _cancel;

    private int? _cpuCores;
    private int? _memoryGigabytes;

    private ushort _metricsPort = 5000;

    private bool ParseArguments(string[] args)
    {
        var showHelp = false;
        var debugger = false;

        var options = new OptionSet
            {
                "Usage: Consumer.exe --cpu-cores 10 --memory-gb 16",
                "Consumes CPU and memory resources.",
                "",
                { "h|?|help", "Displays usage instructions.", val => showHelp = val != null },
                "",
                "Resource targets",
                { "cpu-cores=", "How many CPU cores worth of CPU time to consume.", (int val) => _cpuCores = val },
                { "memory-gb=", "How many GB of memory to consume and keep actively accessing.", (int val) => _memoryGigabytes = val },
                "Monitoring",
                { "metrics-port=", $"Port number to publish metrics on. Defaults to {_metricsPort}.", (ushort val) => _metricsPort = val },
                "",
                { "debugger", "Requests a debugger to be attached before data processing starts.", val => debugger = val != null, true }
            };

        List<string> remainingOptions;

        try
        {
            remainingOptions = options.Parse(args);

            if (args.Length == 0 || showHelp)
            {
                options.WriteOptionDescriptions(Console.Out);
                return false;
            }

            if (_cpuCores == null && _memoryGigabytes == null)
                throw new OptionException("You must consume at least one type of resource.", "cpu-cores");
        }
        catch (OptionException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("For usage instructions, use the --help command line parameter.");
            return false;
        }

        if (remainingOptions.Count != 0)
        {
            Console.WriteLine("Unknown command line parameters: {0}", string.Join(" ", remainingOptions.ToArray()));
            Console.WriteLine("For usage instructions, use the --help command line parameter.");
            return false;
        }

        if (debugger)
            Debugger.Launch();

        return true;
    }
}