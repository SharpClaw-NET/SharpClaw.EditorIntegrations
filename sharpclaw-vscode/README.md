# SharpClaw for VS Code

SharpClaw for VS Code connects a running SharpClaw backend to the active VS
Code workspace. The extension keeps the editor-specific bridge inside VS Code
and passes file, selection, diagnostics, diff, build, terminal, and chat actions
through the backend editor session instead of requiring the backend process to
own the workspace directly.

Use this extension when you want VS Code to participate in SharpClaw agent
workflows. Start the SharpClaw backend that you operate, open VS Code, then use
the SharpClaw connect command or enable automatic connection from the extension
settings. The extension can discover backend runtime authentication from a
backend instance id, or it can use a configured host, port, and explicit API key
file path.

The extension source and issue tracker live in the SharpClaw.EditorIntegrations
repository at
[https://github.com/SharpClaw-NET/SharpClaw.EditorIntegrations](https://github.com/SharpClaw-NET/SharpClaw.EditorIntegrations).
