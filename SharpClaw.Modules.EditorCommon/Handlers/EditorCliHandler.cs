using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.EditorCommon.Handlers;

/// <summary>Runs the editor session CLI through the typed session actions.</summary>
public sealed class EditorCliHandler(
    IServiceScopeFactory scopeFactory,
    IHostActionEntry hostActionEntry) : IModuleCliHandler
{
    public static IReadOnlyList<string> Commands { get; } = ["editorsession"];

    public async ValueTask<ModuleCliResult> ExecuteAsync(
        ModuleCliInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            var operation = ParseOperation(invocation.Arguments, out var sessionId, out var payload);
            using var scope = scopeFactory.CreateScope();
            var gateway = scope.ServiceProvider.GetRequiredService<EditorSessionActionGateway>();
            var result = await gateway.ExecuteAsync(
                hostActionEntry,
                invocation.HostActionContext,
                new EditorSessionAction(operation, sessionId, payload),
                ct);

            var text = operation == EditorSessionOperation.Delete
                ? result.ValueKind == JsonValueKind.True && result.GetBoolean()
                    ? "Done."
                    : "Not found."
                : result.GetRawText();
            return Success(text);
        }
        catch (ArgumentException exception)
        {
            return Failure(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(exception.Message);
        }
    }

    private static EditorSessionOperation ParseOperation(
        IReadOnlyList<string> arguments,
        out Guid? sessionId,
        out JsonElement payload)
    {
        sessionId = null;
        payload = JsonSerializer.SerializeToElement(new { });

        var operation = arguments.FirstOrDefault()?.ToLowerInvariant()
            ?? throw new ArgumentException(
                "Use editorsession list, get <id>, or delete <id>.");

        switch (operation)
        {
            case "list":
                return EditorSessionOperation.List;
            case "get":
                sessionId = ParseId(arguments, "get");
                payload = JsonSerializer.SerializeToElement(new { id = sessionId });
                return EditorSessionOperation.Get;
            case "delete":
                sessionId = ParseId(arguments, "delete");
                payload = JsonSerializer.SerializeToElement(new { id = sessionId });
                return EditorSessionOperation.Delete;
            default:
                throw new ArgumentException(
                    $"Unknown editorsession operation '{operation}'.");
        }
    }

    private static Guid ParseId(IReadOnlyList<string> arguments, string operation) =>
        arguments.Count > 1 && Guid.TryParse(arguments[1], out var id)
            ? id
            : throw new ArgumentException(
                $"Use editorsession {operation} <id>.");

    private static ModuleCliResult Success(string text) =>
        new(true, [new ModuleCliOutput("stdout", text)]);

    private static ModuleCliResult Failure(string text) =>
        new(false, [new ModuleCliOutput("stderr", text)],
            new ExecutionError("invalid_arguments", text));
}
