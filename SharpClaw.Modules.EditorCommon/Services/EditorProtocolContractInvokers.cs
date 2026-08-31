using System.Text.Json;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.EditorCommon.Models;

namespace SharpClaw.Modules.EditorCommon.Services;

/// <summary>Executes the typed connection read action owned by EditorCommon.</summary>
public sealed class EditorBridgeConnectionReadTerminal(EditorBridgeService bridge)
    : IHostActionEntryTerminal<EditorBridgeConnectionReadAction, EditorBridgeConnectionReadResult>
{
    public Guid TerminalId => EditorProtocolContracts.BridgeConnectionReadTerminalId;

    public ValueTask<EditorBridgeConnectionReadResult> InvokeAsync(
        ActionContext<EditorBridgeConnectionReadAction> context,
        CancellationToken ct)
    {
        var connection = context.Action.SessionId is { } sessionId
            ? bridge.GetConnection(sessionId)
            : null;
        var connections = context.Action.SessionId is null
            ? bridge.GetConnections().Select(ToSummary).ToArray()
            : Array.Empty<EditorBridgeConnectionSummary>();

        return ValueTask.FromResult(new EditorBridgeConnectionReadResult(
            connection is not null,
            connection is null ? null : ToSummary(connection),
            connections));
    }

    private static EditorBridgeConnectionSummary ToSummary(EditorConnection connection) =>
        new(
            connection.ConnectionId,
            connection.SessionId,
            connection.EditorType.ToString(),
            connection.EditorVersion,
            connection.WorkspacePath,
            connection.State,
            connection.ConnectedAt);
}

/// <summary>Executes read-only typed editor bridge requests.</summary>
public sealed class EditorBridgeRequestReadTerminal(EditorBridgeService bridge)
    : IHostActionEntryTerminal<EditorBridgeRequestAction, EditorActionResponse>
{
    public Guid TerminalId => EditorProtocolContracts.BridgeRequestReadTerminalId;

    public ValueTask<EditorActionResponse> InvokeAsync(
        ActionContext<EditorBridgeRequestAction> context,
        CancellationToken ct) =>
        EditorBridgeRequestTerminalSupport.InvokeAsync(bridge, context.Action, mutation: false, ct);
}

/// <summary>Executes irreversible typed editor bridge requests.</summary>
public sealed class EditorBridgeRequestMutationTerminal(EditorBridgeService bridge)
    : IHostActionEntryTerminal<EditorBridgeRequestAction, EditorActionResponse>
{
    public Guid TerminalId => EditorProtocolContracts.BridgeRequestMutationTerminalId;

    public ValueTask<EditorActionResponse> InvokeAsync(
        ActionContext<EditorBridgeRequestAction> context,
        CancellationToken ct) =>
        EditorBridgeRequestTerminalSupport.InvokeAsync(bridge, context.Action, mutation: true, ct);
}

internal static class EditorBridgeRequestTerminalSupport
{
    public static ValueTask<EditorActionResponse> InvokeAsync(
        EditorBridgeService bridge,
        EditorBridgeRequestAction action,
        bool mutation,
        CancellationToken ct)
    {
        var operationAllowed = mutation
            ? EditorProtocolContracts.IsMutationBridgeOperation(action.Action)
            : EditorProtocolContracts.IsReadBridgeOperation(action.Action);
        if (!operationAllowed)
        {
            throw new ArgumentException(
                $"Editor operation '{action.Action}' is not valid for the selected bridge action.");
        }

        var connection = bridge.GetRequiredConnection(
            action.SessionId,
            action.ExpectedEditorKey);
        return new ValueTask<EditorActionResponse>(bridge.SendRequestAsync(
            connection,
            action.Action,
            ToDictionary(action.Parameters),
            ct));
    }

    private static Dictionary<string, object?>? ToDictionary(
        IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        return parameters.ToDictionary(
            item => item.Key,
            item => (object?)item.Value.Clone(),
            StringComparer.Ordinal);
    }
}

/// <summary>Executes the read-only editor session actions.</summary>
public sealed class EditorSessionReadTerminal(
    EditorSessionService sessions,
    EditorBridgeService bridge)
    : IHostActionEntryTerminal<EditorSessionAction, JsonElement>
{
    public Guid TerminalId => EditorProtocolContracts.SessionReadTerminalId;

    public async ValueTask<JsonElement> InvokeAsync(
        ActionContext<EditorSessionAction> context,
        CancellationToken ct)
    {
        var action = context.Action;
        return action.Operation switch
        {
            EditorSessionOperation.Get => ToElement(
                action.SessionId is { } id
                    ? ConnectedOptional(await sessions.GetByIdAsync(id, ct))
                    : null),
            EditorSessionOperation.List => ToElement(
                (await sessions.ListAsync(ct)).Select(ConnectedRequired).ToArray()),
            EditorSessionOperation.ListIds => ToElement(
                (await sessions.ListAsync(ct)).Select(session => session.Id).ToArray()),
            EditorSessionOperation.LookupItems => ToElement(
                (await sessions.ListAsync(ct))
                    .Select(session => new EditorSessionLookupItem(session.Id, session.Name))
                    .ToArray()),
            _ => throw new ArgumentException(
                $"Editor session operation '{action.Operation}' is not a read operation."),
        };
    }

    private EditorSessionResponse? ConnectedOptional(EditorSessionResponse? response) =>
        response is null
            ? null
            : response with { IsConnected = bridge.GetConnection(response.Id) is not null };

    private EditorSessionResponse ConnectedRequired(EditorSessionResponse response) =>
        response with { IsConnected = bridge.GetConnection(response.Id) is not null };

    internal static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, EditorJson.Options);
}

/// <summary>Executes the mutable editor session actions.</summary>
public sealed class EditorSessionMutationTerminal(EditorSessionService sessions)
    : IHostActionEntryTerminal<EditorSessionAction, JsonElement>
{
    public Guid TerminalId => EditorProtocolContracts.SessionMutationTerminalId;

    public async ValueTask<JsonElement> InvokeAsync(
        ActionContext<EditorSessionAction> context,
        CancellationToken ct)
    {
        var action = context.Action;
        return action.Operation switch
        {
            EditorSessionOperation.GetOrCreate => await GetOrCreateAsync(action.Payload, ct),
            EditorSessionOperation.Create => EditorSessionReadTerminal.ToElement(
                await sessions.CreateAsync(
                    Deserialize<CreateEditorSessionRequest>(action.Payload), ct)),
            EditorSessionOperation.Update => EditorSessionReadTerminal.ToElement(
                action.SessionId is { } id
                    ? await sessions.UpdateAsync(
                        id,
                        Deserialize<UpdateEditorSessionRequest>(action.Payload),
                        ct)
                    : throw new ArgumentException("A session ID is required.")),
            EditorSessionOperation.Delete => EditorSessionReadTerminal.ToElement(
                action.SessionId is { } id
                    ? await sessions.DeleteAsync(id, ct)
                    : throw new ArgumentException("A session ID is required.")),
            _ => throw new ArgumentException(
                $"Editor session operation '{action.Operation}' is not a mutation."),
        };
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(EditorJson.Options)
        ?? throw new ArgumentException($"Could not deserialize {typeof(T).Name}.");

    private async Task<JsonElement> GetOrCreateAsync(
        JsonElement payload,
        CancellationToken ct)
    {
        var request = Deserialize<CreateEditorSessionRequest>(payload);
        var editorType = Enum.TryParse<EditorType>(
            request.EditorKey,
            ignoreCase: true,
            out var parsed)
            ? parsed
            : EditorType.Other;
        var session = await sessions.GetOrCreateAsync(
            request.Name,
            editorType,
            request.EditorVersion,
            request.WorkspacePath,
            ct);
        return EditorSessionReadTerminal.ToElement(
            EditorSessionService.ToResponse(session));
    }
}

/// <summary>Invokes EditorCommon connection actions through the host entry.</summary>
public sealed class EditorBridgeActionGateway(
    EditorBridgeConnectionReadTerminal connectionTerminal)
{
    public async ValueTask<EditorBridgeConnectionReadResult> ReadAsync(
        IHostActionEntry hostActionEntry,
        HostActionEntryRequestContext hostContext,
        Guid? sessionId,
        CancellationToken ct)
    {
        var outcome = await hostActionEntry.InvokeAsync(
            new HostActionEntryRequest<
                EditorBridgeConnectionReadAction,
                EditorBridgeConnectionReadResult>(
                EditorProtocolContracts.BridgeConnectionReadDescriptor,
                new EditorBridgeConnectionReadAction(sessionId),
                hostContext),
            connectionTerminal,
            ct);
        return RequireCompleted(outcome, "The editor connection read action did not complete.");
    }

    private static TResult RequireCompleted<TResult>(
        IActionOutcome<TResult> outcome,
        string message)
    {
        if (outcome.Kind != ActionOutcomeKind.Completed || outcome.Result is null)
            throw new InvalidOperationException(outcome.Error?.Message ?? message);
        return outcome.Result;
    }
}

/// <summary>Invokes EditorCommon session actions through the host entry.</summary>
public sealed class EditorSessionActionGateway(
    EditorSessionReadTerminal readTerminal,
    EditorSessionMutationTerminal mutationTerminal)
{
    public async ValueTask<JsonElement> ExecuteAsync(
        IHostActionEntry hostActionEntry,
        HostActionEntryRequestContext hostContext,
        EditorSessionAction action,
        CancellationToken ct)
    {
        var isRead = action.Operation is
            EditorSessionOperation.Get or
            EditorSessionOperation.List or
            EditorSessionOperation.ListIds or
            EditorSessionOperation.LookupItems;
        var descriptor = isRead
            ? EditorProtocolContracts.SessionReadDescriptor
            : EditorProtocolContracts.SessionMutationDescriptor;

        IActionOutcome<JsonElement> outcome = isRead
            ? await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<EditorSessionAction, JsonElement>(
                    descriptor,
                    action,
                    hostContext),
                readTerminal,
                ct)
            : await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<EditorSessionAction, JsonElement>(
                    descriptor,
                    action,
                    hostContext),
                mutationTerminal,
                ct);

        if (outcome.Kind != ActionOutcomeKind.Completed ||
            outcome.Result.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(
                outcome.Error?.Message ?? "The editor session action did not complete.");
        return outcome.Result;
    }
}

/// <summary>Creates the editor chat context contribution from active connections.</summary>
public sealed class EditorChatContextContributor(EditorBridgeService bridge)
    : IChatContextContributor
{
    public ValueTask<ChatContextContribution> ContributeAsync(
        ChatContextRequest request,
        CancellationToken ct)
    {
        var connections = bridge.GetConnections();
        var summary = connections.Count == 0
            ? "(none)"
            : string.Join(", ", connections.Select(connection =>
            {
                var text = connection.EditorType.ToString();
                if (connection.EditorVersion is not null)
                    text += $" {connection.EditorVersion}";
                if (connection.WorkspacePath is not null)
                    text += $" workspace={connection.WorkspacePath}";
                return text;
            }));

        return ValueTask.FromResult(new ChatContextContribution(
            [new SystemPromptSegment("editor", summary)],
            [],
            []));
    }
}

/// <summary>Provides shared JSON options for editor action payloads.</summary>
internal static class EditorJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed record EditorSessionLookupItem(Guid Id, string Name);
