using System.Text.Json;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.EditorCommon;

/// <summary>Public contract shared by the editor bridge and editor modules.</summary>
public sealed class EditorModuleContract
{
}

/// <summary>Identifies one connection read request.</summary>
public sealed record EditorBridgeConnectionReadAction(Guid? SessionId);

/// <summary>Describes one connected editor without exposing its transport.</summary>
public sealed record EditorBridgeConnectionSummary(
    string ConnectionId,
    Guid SessionId,
    string EditorKey,
    string? EditorVersion,
    string? WorkspacePath,
    string SocketState,
    DateTimeOffset ConnectedAt);

/// <summary>Returns one connection or the current connection list.</summary>
public sealed record EditorBridgeConnectionReadResult(
    bool Exists,
    EditorBridgeConnectionSummary? Connection,
    IReadOnlyList<EditorBridgeConnectionSummary> Connections);

/// <summary>Requests one typed editor bridge operation.</summary>
public sealed record EditorBridgeRequestAction(
    Guid SessionId,
    string ExpectedEditorKey,
    string Action,
    IReadOnlyDictionary<string, JsonElement>? Parameters);

/// <summary>Identifies one editor session operation.</summary>
public enum EditorSessionOperation
{
    Create,
    GetOrCreate,
    Get,
    List,
    Update,
    Delete,
    ListIds,
    LookupItems,
}

/// <summary>Requests one editor session operation through the host entry.</summary>
public sealed record EditorSessionAction(
    EditorSessionOperation Operation,
    Guid? SessionId,
    JsonElement Payload);

/// <summary>Defines the public editor module contract and typed action entries.</summary>
public static class EditorProtocolContracts
{
    public const string ContractName = "sharpclaw.editor";
    public const string EditorCommonModuleId = "sharpclaw_editor_common";
    public const string BridgeConnectionReadActionName = "editor.bridge.connection.read";
    public const string BridgeRequestReadActionName = "editor.bridge.request.read";
    public const string BridgeRequestMutationActionName = "editor.bridge.request.mutate";
    public const string SessionReadActionName = "editor.session.read";
    public const string SessionMutationActionName = "editor.session.mutate";

    public static bool IsReadBridgeOperation(string operation) => operation switch
    {
        "read_file" or "get_open_files" or "get_selection" or "get_diagnostics" => true,
        _ => false,
    };

    public static bool IsMutationBridgeOperation(string operation) => operation switch
    {
        "write_file" or "apply_edit" or "create_file" or "delete_file"
            or "show_diff" or "run_build" or "run_terminal" => true,
        _ => false,
    };

    public static readonly Guid BridgeConnectionReadTerminalId =
        Guid.Parse("7f1e1ca7-ef3f-4b4c-9b2f-5a7a2c8f0301");
    public static readonly Guid BridgeRequestReadTerminalId =
        Guid.Parse("7f1e1ca7-ef3f-4b4c-9b2f-5a7a2c8f0302");
    public static readonly Guid BridgeRequestMutationTerminalId =
        Guid.Parse("7f1e1ca7-ef3f-4b4c-9b2f-5a7a2c8f0303");
    public static readonly Guid SessionReadTerminalId =
        Guid.Parse("7f1e1ca7-ef3f-4b4c-9b2f-5a7a2c8f0304");
    public static readonly Guid SessionMutationTerminalId =
        Guid.Parse("7f1e1ca7-ef3f-4b4c-9b2f-5a7a2c8f0305");

    private static readonly ActionInterceptionCapabilities SafeCapabilities =
        ActionInterceptionCapabilities.Inspect
        | ActionInterceptionCapabilities.Cancel
        | ActionInterceptionCapabilities.Observe;

    private static readonly ActionRepeatPolicy ReadRepeatPolicy =
        new(ActionRepeatKind.Idempotent, 3, TimeSpan.FromMilliseconds(50), "editor.read");

    private static readonly ActionRepeatPolicy MutationRepeatPolicy =
        new(ActionRepeatKind.None, 1, TimeSpan.Zero, "editor.mutation");

    public static readonly ActionDescriptor<EditorBridgeConnectionReadAction, EditorBridgeConnectionReadResult>
        BridgeConnectionReadDescriptor = CreateDescriptor<
            EditorBridgeConnectionReadAction,
            EditorBridgeConnectionReadResult>(
            BridgeConnectionReadActionName,
            "editor.bridge",
            false,
            ReadRepeatPolicy);

    public static readonly ActionDescriptor<EditorBridgeRequestAction, EditorActionResponse>
        BridgeRequestReadDescriptor = CreateDescriptor<EditorBridgeRequestAction, EditorActionResponse>(
            BridgeRequestReadActionName,
            "editor.bridge",
            false,
            ReadRepeatPolicy);

    public static readonly ActionDescriptor<EditorBridgeRequestAction, EditorActionResponse>
        BridgeRequestMutationDescriptor = CreateDescriptor<EditorBridgeRequestAction, EditorActionResponse>(
            BridgeRequestMutationActionName,
            "editor.bridge",
            true,
            MutationRepeatPolicy);

    public static readonly ActionDescriptor<EditorSessionAction, JsonElement>
        SessionReadDescriptor = CreateDescriptor<EditorSessionAction, JsonElement>(
            SessionReadActionName,
            "editor.session",
            false,
            ReadRepeatPolicy);

    public static readonly ActionDescriptor<EditorSessionAction, JsonElement>
        SessionMutationDescriptor = CreateDescriptor<EditorSessionAction, JsonElement>(
            SessionMutationActionName,
            "editor.session",
            true,
            MutationRepeatPolicy);

    private static readonly IReadOnlyList<ModuleStorageOperationDescriptor> StorageOperations =
    [
        new(ModuleStorageOperations.Get),
        new(ModuleStorageOperations.Upsert),
        new(ModuleStorageOperations.BatchUpsert),
        new(ModuleStorageOperations.Delete),
        new(ModuleStorageOperations.BatchDelete),
        new(ModuleStorageOperations.List),
        new(ModuleStorageOperations.Query),
    ];

    public static ModuleStorageContractDescriptor SessionStorage { get; } =
        new(
            EditorCommonModuleId,
            "editor_sessions",
            StorageOperations,
            "Editor session records keyed by connected editor workspace.",
            [
                new("name", ModuleStorageIndexValueKind.String),
                new("editorType", ModuleStorageIndexValueKind.String),
                new("workspacePath", ModuleStorageIndexValueKind.String),
                new("editorWorkspace", ModuleStorageIndexValueKind.String),
            ],
            MaxDocumentBytes: 131_072,
            MaxBatchSize: 100);

    private static ActionDescriptor<TAction, TResult> CreateDescriptor<TAction, TResult>(
        string actionName,
        string category,
        bool irreversible,
        ActionRepeatPolicy repeatPolicy)
    {
        var key = new SharpClawActionKey(actionName);
        return new ActionDescriptor<TAction, TResult>(
            key,
            1,
            category,
            SafeCapabilities,
            ContainsSensitiveData: irreversible,
            HasIrreversibleEffects: irreversible,
            repeatPolicy,
            ContinuationPolicy: null,
            DefaultTimeout: TimeSpan.FromSeconds(30))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(key, 1, typeof(TAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(key, 1, typeof(TResult)),
        };
    }
}
