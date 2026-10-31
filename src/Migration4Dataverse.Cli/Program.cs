using Migration4Dataverse.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var modulesPath = Path.Combine(AppContext.BaseDirectory, "modules");
    using var runtime = new PowerShellCliRuntime(modulesPath, Console.Out, Console.Error);
    var rootCommand = new CliCommandApp(runtime, cancellation.Token).Build();

    if (args.Length == 0)
    {
        rootCommand.Parse(["--help"]).Invoke();
        return ExitCodes.InvalidArgumentsOrJob;
    }

    var parseResult = rootCommand.Parse(args);
    if (parseResult.Errors.Count > 0)
    {
        var message = string.Join(Environment.NewLine, parseResult.Errors.Select(error => error.Message));
        if (args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)))
        {
            new CliEventWriter(Console.Out).Write(
                "error",
                new Dictionary<string, object?>
                {
                    ["message"] = message,
                    ["exitCode"] = ExitCodes.InvalidArgumentsOrJob
                });
        }
        else
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine("Run 'm4d --help' to see available commands and options.");
        }
        return ExitCodes.InvalidArgumentsOrJob;
    }

    return parseResult.Invoke();
}
catch (Exception exception)
{
    if (args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)))
    {
        new CliEventWriter(Console.Out).Write(
            "error",
            new Dictionary<string, object?>
            {
                ["message"] = exception.Message,
                ["exitCode"] = ExitCodes.RuntimeStartupFailure
            });
    }
    else
    {
        Console.Error.WriteLine(exception.Message);
    }
    return ExitCodes.RuntimeStartupFailure;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
