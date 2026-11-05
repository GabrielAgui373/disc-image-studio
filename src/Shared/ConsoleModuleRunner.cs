using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.Generation;
using DiscImageStudio.Services;

namespace DiscImageStudio;

internal static class ConsoleModuleRunner
{
    internal static DiscModuleCatalog Catalog { get; } = new(
        [new CdDiscModule(), new DvdDiscModule(), new BluRayDiscModule(BluRayImageLoader.Load)]);

    private static IDiscGenerationService GenerationService { get; } = new DiscGenerationService();

    internal static int Run(string[] arguments)
    {
        if (TypedGenerationCommandLine.TryParse(arguments, out TypedGenerationCommand? generation))
        {
            return RunGeneration(generation!);
        }

        DiscJobRequest request = new(arguments[0], arguments.Skip(1));
        IOpticalDiscModule module = Catalog.Resolve(request.Command);
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            if (!module.Descriptor.SupportsCancellation) return;
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            DiscJobResult result = module.ExecuteAsync(request, new ConsoleProgress(), cancellation.Token)
                .GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(result.Summary)) Console.WriteLine(result.Summary);
            return result.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int RunGeneration(TypedGenerationCommand command)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            GenerationPlan plan = GenerationService.PlanAsync(command.Request, cancellation.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine(plan.Summary);
            GenerationService.GenerateFileAsync(
                    plan,
                    command.OutputPath,
                    new ConsoleGenerationProgress(),
                    cancellation.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine($"Generation complete: {Path.GetFullPath(command.OutputPath)}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private sealed class ConsoleProgress : IProgress<DiscJobProgress>
    {
        public void Report(DiscJobProgress value)
        {
            if (!string.IsNullOrWhiteSpace(value.Message)) Console.WriteLine(value.Message);
        }
    }

    private sealed class ConsoleGenerationProgress : IProgress<GenerationProgress>
    {
        private int _lastPercent = -1;

        public void Report(GenerationProgress value)
        {
            int percent = (int)Math.Clamp(value.Fraction * 100, 0, 100);
            if (percent == _lastPercent && value.Stage != "complete")
            {
                return;
            }

            _lastPercent = percent;
            Console.WriteLine($"{value.MessageCode}: {percent}%");
        }
    }
}
