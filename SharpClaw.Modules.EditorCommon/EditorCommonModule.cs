using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.EditorCommon.Handlers;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.EditorCommon;

/// <summary>Provides the shared editor bridge and editor session actions.</summary>
public sealed class EditorCommonModule : ISharpClawModule, ISharpClawApplicationModule
{
    public ModuleIdentity Identity { get; } = new(
        EditorProtocolContracts.EditorCommonModuleId,
        "Editor Common",
        "edc");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<EditorSessionStore>();
        module.Services.AddSingleton<EditorBridgeService>();
        module.Services.AddScoped<EditorSessionService>();
        module.Services.AddScoped<EditorBridgeConnectionReadTerminal>();
        module.Services.AddScoped<EditorBridgeRequestReadTerminal>();
        module.Services.AddScoped<EditorBridgeRequestMutationTerminal>();
        module.Services.AddScoped<EditorSessionReadTerminal>();
        module.Services.AddScoped<EditorSessionMutationTerminal>();
        module.Services.AddScoped<EditorBridgeActionGateway>();
        module.Services.AddScoped<EditorSessionActionGateway>();
        module.Services.AddSingleton<EditorCliHandler>();
        module.Services.AddScoped<EditorEndpointContribution>();
        module.Services.AddScoped<EditorWebSocketEndpointContribution>();
        module.Services.AddScoped<EditorSessionEndpointContribution>();
        module.Services.AddScoped<EditorChatContextContributor>();
        module.Services.AddScoped<IChatContextContributor, EditorChatContextContributor>();

        module.Contracts.Export<EditorModuleContract>(EditorProtocolContracts.ContractName);
        module.Storage.Add(EditorProtocolContracts.SessionStorage);

        module.Actions.Add(EditorProtocolContracts.BridgeConnectionReadDescriptor);
        module.AddActionEntry<
            EditorBridgeConnectionReadAction,
            EditorBridgeConnectionReadResult,
            EditorBridgeConnectionReadTerminal>(
            EditorProtocolContracts.BridgeConnectionReadDescriptor,
            EditorProtocolContracts.BridgeConnectionReadTerminalId);

        module.Actions.Add(EditorProtocolContracts.BridgeRequestReadDescriptor);
        module.AddActionEntry<
            EditorBridgeRequestAction,
            SharpClaw.Contracts.DTOs.Editor.EditorActionResponse,
            EditorBridgeRequestReadTerminal>(
            EditorProtocolContracts.BridgeRequestReadDescriptor,
            EditorProtocolContracts.BridgeRequestReadTerminalId);

        module.Actions.Add(EditorProtocolContracts.BridgeRequestMutationDescriptor);
        module.AddActionEntry<
            EditorBridgeRequestAction,
            SharpClaw.Contracts.DTOs.Editor.EditorActionResponse,
            EditorBridgeRequestMutationTerminal>(
            EditorProtocolContracts.BridgeRequestMutationDescriptor,
            EditorProtocolContracts.BridgeRequestMutationTerminalId);

        module.Actions.Add(EditorProtocolContracts.SessionReadDescriptor);
        module.AddActionEntry<
            EditorSessionAction,
            System.Text.Json.JsonElement,
            EditorSessionReadTerminal>(
            EditorProtocolContracts.SessionReadDescriptor,
            EditorProtocolContracts.SessionReadTerminalId);

        module.Actions.Add(EditorProtocolContracts.SessionMutationDescriptor);
        module.AddActionEntry<
            EditorSessionAction,
            System.Text.Json.JsonElement,
            EditorSessionMutationTerminal>(
            EditorProtocolContracts.SessionMutationDescriptor,
            EditorProtocolContracts.SessionMutationTerminalId);

        module.Chat.AddContextContributor<EditorChatContextContributor>();
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        application.Endpoints.AddHttp<EditorEndpointContribution>(
            EditorEndpointContribution.SessionsRoute);
        application.Endpoints.AddWebSocket<EditorWebSocketEndpointContribution>(
            EditorWebSocketEndpointContribution.WebSocketRoute);
        foreach (var route in EditorSessionEndpointContribution.EndpointRoutes)
            application.Endpoints.AddHttp<EditorSessionEndpointContribution>(route);
        foreach (var command in EditorCliHandler.Commands)
        {
            application.Cli.Add<EditorCliHandler>(new ModuleCliCommandDescriptor(
                command,
                command.Equals("editorsession", StringComparison.Ordinal)
                    ? ["editor", "es"]
                    : [],
                "Manage editor sessions.",
                new JsonSchemaReference("sharpclaw.editor.cli.arguments", 1),
                new JsonSchemaReference("sharpclaw.editor.cli.result", 1)));
        }
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
