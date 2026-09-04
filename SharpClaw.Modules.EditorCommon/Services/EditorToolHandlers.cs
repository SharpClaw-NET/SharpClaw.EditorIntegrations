using System.Text.Json;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.EditorCommon.Services;

/// <summary>Builds the stable tool definitions shared by both editor modules.</summary>
public static class EditorToolCatalog
{
    public static IReadOnlyList<ToolDescriptor> Create(
        string prefix,
        string editorName) =>
    [
        new($"{prefix}read_file",
            $"Read file content from a connected {editorName} instance. Optional startLine/endLine for partial reads.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"File path relative to workspace root."},"startLine":{"type":"integer","description":"Optional start line (1-based)."},"endLine":{"type":"integer","description":"Optional end line (1-based, inclusive)."}},"required":["targetId","filePath"]}
            """)),
        new($"{prefix}write_file",
            $"Write full content to a connected {editorName} workspace.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"File path relative to workspace root."},"content":{"type":"string","description":"Full file content to write."}},"required":["targetId","filePath","content"]}
            """),
            ContainsSensitiveData: true),
        new($"{prefix}get_open_files",
            $"List open files and tabs in the connected {editorName} instance.",
            ResourceSchema()),
        new($"{prefix}get_selection",
            $"Get the active file, cursor position, and selected text in {editorName}.",
            ResourceSchema()),
        new($"{prefix}get_diagnostics",
            $"Get errors and warnings from {editorName}. Optional filePath scopes the result.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"Optional file path to scope results."}},"required":["targetId"]}
            """)),
        new($"{prefix}apply_edit",
            $"Replace a line range with new text in {editorName}.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"File path relative to workspace root."},"startLine":{"type":"integer","description":"Start line (1-based)."},"endLine":{"type":"integer","description":"End line (1-based, inclusive)."},"newText":{"type":"string","description":"Replacement text."}},"required":["targetId","filePath","startLine","endLine","newText"]}
            """),
            ContainsSensitiveData: true),
        new($"{prefix}create_file",
            $"Create a new file in the {editorName} workspace.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"File path relative to workspace root."},"content":{"type":"string","description":"Initial file content."}},"required":["targetId","filePath"]}
            """),
            ContainsSensitiveData: true),
        new($"{prefix}delete_file",
            $"Delete a file from the {editorName} workspace.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"File path relative to workspace root."}},"required":["targetId","filePath"]}
            """),
            ContainsSensitiveData: true),
        new($"{prefix}show_diff",
            $"Show a diff view in {editorName} for user review.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"filePath":{"type":"string","description":"File path relative to workspace root."},"proposedContent":{"type":"string","description":"Proposed file content."},"diffTitle":{"type":"string","description":"Diff view title."}},"required":["targetId","filePath","proposedContent"]}
            """),
            ContainsSensitiveData: true),
        new($"{prefix}run_build",
            $"Run a build task in the connected {editorName} instance and return output.",
            ResourceSchema()),
        new($"{prefix}run_terminal",
            $"Run a command in the {editorName} integrated terminal.",
            Schema("""
            {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."},"command":{"type":"string","description":"Command to run."},"workingDirectory":{"type":"string","description":"Working directory."}},"required":["targetId","command"]}
            """),
            ContainsSensitiveData: true),
    ];

    public static bool IsMutation(string operation) => operation switch
    {
        "write_file" or "apply_edit" or "create_file" or "delete_file"
            or "show_diff" or "run_build" or "run_terminal" => true,
        _ => false,
    };

    public static bool IsKnown(string operation) => operation switch
    {
        "read_file" or "write_file" or "get_open_files" or "get_selection"
            or "get_diagnostics" or "apply_edit" or "create_file" or "delete_file"
            or "show_diff" or "run_build" or "run_terminal" => true,
        _ => false,
    };

    private static JsonElement ResourceSchema() => Schema("""
        {"type":"object","properties":{"targetId":{"type":"string","description":"EditorSession GUID."}},"required":["targetId"]}
        """);

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>Uses only host-issued typed actions to call EditorCommon.</summary>
public abstract class EditorToolHandler(
    IHostActionEntry hostActionEntry,
    string expectedEditorKey,
    string displayName,
    string toolPrefix) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            var operation = invocation.ToolName.StartsWith(toolPrefix, StringComparison.Ordinal)
                ? invocation.ToolName[toolPrefix.Length..]
                : invocation.ToolName;
            if (!EditorToolCatalog.IsKnown(operation))
                return ToolResult.Error($"Unknown editor tool '{invocation.ToolName}'.");

            var sessionId = ReadSessionId(invocation.Arguments);
            var connection = await hostActionEntry.InvokeCrossSidecarAsync(
                new ModuleCrossSidecarActionEntryRequest<
                    EditorBridgeConnectionReadAction,
                    EditorBridgeConnectionReadResult>(
                    EditorProtocolContracts.BridgeConnectionReadDescriptor,
                    new EditorBridgeConnectionReadAction(sessionId)),
                ct);
            if (connection.Kind != ActionOutcomeKind.Completed || connection.Result is null)
                return ToolResult.Error(connection.Error?.Message ?? "The editor connection read failed.");

            if (!connection.Result.Exists || connection.Result.Connection is null)
                return ToolResult.Error($"No editor is connected for session {sessionId}.");

            if (!string.Equals(
                    connection.Result.Connection.EditorKey,
                    expectedEditorKey,
                    StringComparison.Ordinal))
            {
                return ToolResult.Error(
                    $"Session {sessionId} is connected to {connection.Result.Connection.EditorKey}, not {displayName}.");
            }

            var parameters = ReadParameters(invocation.Arguments);
            var descriptor = EditorToolCatalog.IsMutation(operation)
                ? EditorProtocolContracts.BridgeRequestMutationDescriptor
                : EditorProtocolContracts.BridgeRequestReadDescriptor;
            var response = await hostActionEntry.InvokeCrossSidecarAsync(
                new ModuleCrossSidecarActionEntryRequest<
                    EditorBridgeRequestAction,
                    EditorActionResponse>(
                    descriptor,
                    new EditorBridgeRequestAction(
                        sessionId,
                        expectedEditorKey,
                        operation,
                        parameters)),
                ct);
            if (response.Kind != ActionOutcomeKind.Completed || response.Result is null)
                return ToolResult.Error(response.Error?.Message ?? "The editor request failed.");

            return response.Result.Success
                ? ToolResult.Text(response.Result.Data ?? $"{displayName} action '{operation}' completed.")
                : ToolResult.Error(response.Result.Error ?? $"{displayName} action '{operation}' failed.");
        }
        catch (ArgumentException exception)
        {
            return ToolResult.Error(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolResult.Error(exception.Message);
        }
    }

    private static Guid ReadSessionId(JsonElement arguments)
    {
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name.Equals("targetId", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(property.Value.GetString(), out var id))
            {
                return id;
            }
        }

        throw new ArgumentException("Missing or invalid 'targetId' parameter.");
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadParameters(
        JsonElement arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!property.Name.Equals("targetId", StringComparison.OrdinalIgnoreCase))
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }
}

/// <summary>Tool handler for the Visual Studio 2026 module.</summary>
public sealed class VS2026EditorToolHandler(IHostActionEntry hostActionEntry)
    : EditorToolHandler(hostActionEntry, "VisualStudio2026", "Visual Studio 2026", "vs26_")
{
}

/// <summary>Tool handler for the Visual Studio Code module.</summary>
public sealed class VSCodeEditorToolHandler(IHostActionEntry hostActionEntry)
    : EditorToolHandler(hostActionEntry, "VisualStudioCode", "VS Code", "vsc_")
{
}
