# SharpClaw for Visual Studio 2026

SharpClaw for Visual Studio 2026 connects a running SharpClaw backend to the
Visual Studio editor. It keeps the IDE-specific bridge in the extension and
passes file, selection, diagnostics, build, terminal, and chat actions through
the backend editor session instead of requiring the backend process to own the
workspace directly.

Use this extension when you want Visual Studio to participate in SharpClaw
agent workflows. Start the SharpClaw backend that you operate, open Visual
Studio 2026, then connect through Tools, SharpClaw, Connect. Runtime connection
settings are available under Tools, Options, SharpClaw, and the chat surface is
available from Tools, SharpClaw, SharpClaw Chat.

The extension source and issue tracker live in the SharpClaw.EditorIntegrations
repository at
[https://github.com/SharpClaw-NET/SharpClaw.EditorIntegrations](https://github.com/SharpClaw-NET/SharpClaw.EditorIntegrations).
