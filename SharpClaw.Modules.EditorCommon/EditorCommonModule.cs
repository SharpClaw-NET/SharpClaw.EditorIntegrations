using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.EditorCommon.Handlers;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.EditorCommon;

/// <summary>Provides the shared editor bridge and editor session actions.</summary>
public sealed class EditorCommonModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        EditorProtocolContracts.EditorCommonModuleId,
        "Editor Common",
        "edc");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<EditorSessionStore>();
        services.AddSingleton<EditorBridgeService>();
        services.AddScoped<EditorSessionService>();
        services.AddScoped<EditorBridgeConnectionReadTerminal>();
        services.AddScoped<EditorBridgeRequestReadTerminal>();
        services.AddScoped<EditorBridgeRequestMutationTerminal>();
        services.AddScoped<EditorSessionReadTerminal>();
        services.AddScoped<EditorSessionMutationTerminal>();
        services.AddScoped<EditorBridgeActionGateway>();
        services.AddScoped<EditorSessionActionGateway>();
        services.AddSingleton<EditorCliHandler>();
        services.AddScoped<EditorEndpointContribution>();
        services.AddScoped<EditorWebSocketEndpointContribution>();
        services.AddScoped<EditorSessionEndpointContribution>();
        services.AddScoped<EditorChatContextContributor>();
        services.AddScoped<IChatContextContributor, EditorChatContextContributor>();

        services.ExportContract<EditorModuleContract>(EditorProtocolContracts.ContractName);
        services.AddStorage(EditorProtocolContracts.SessionStorage);

        services.AddAction(EditorProtocolContracts.BridgeConnectionReadDescriptor)
            .UseTerminal<EditorBridgeConnectionReadTerminal>(
                EditorProtocolContracts.BridgeConnectionReadTerminalId);

        services.AddAction(EditorProtocolContracts.BridgeRequestReadDescriptor)
            .UseTerminal<EditorBridgeRequestReadTerminal>(
                EditorProtocolContracts.BridgeRequestReadTerminalId);

        services.AddAction(EditorProtocolContracts.BridgeRequestMutationDescriptor)
            .UseTerminal<EditorBridgeRequestMutationTerminal>(
                EditorProtocolContracts.BridgeRequestMutationTerminalId);

        services.AddAction(EditorProtocolContracts.SessionReadDescriptor)
            .UseTerminal<EditorSessionReadTerminal>(
                EditorProtocolContracts.SessionReadTerminalId);

        services.AddAction(EditorProtocolContracts.SessionMutationDescriptor)
            .UseTerminal<EditorSessionMutationTerminal>(
                EditorProtocolContracts.SessionMutationTerminalId);

        services.AddChatContext<EditorChatContextContributor>();
        services.AddHttpEndpoint<EditorEndpointContribution>(
            EditorEndpointContribution.SessionsRoute);
        services.AddWebSocketEndpoint<EditorWebSocketEndpointContribution>(
            EditorWebSocketEndpointContribution.WebSocketRoute);
        foreach (var route in EditorSessionEndpointContribution.EndpointRoutes)
            services.AddHttpEndpoint<EditorSessionEndpointContribution>(route);
        foreach (var command in EditorCliHandler.Commands)
        {
            services.AddCliCommand<EditorCliHandler>(new CliCommandDescriptor(
                command,
                command.Equals("editorsession", StringComparison.Ordinal)
                    ? ["editor", "es"]
                    : [],
                "Manage editor sessions.",
                new JsonSchemaReference("sharpclaw.editor.cli.arguments", 1),
                new JsonSchemaReference("sharpclaw.editor.cli.result", 1)));
        }
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
