import * as vscode from "vscode";
import { LanguageClient, LanguageClientOptions, ServerOptions, TransportKind } from "vscode-languageclient/node";

const installCommand = "dotnet tool install -g WpfXaml.LanguageServer --prerelease";

let client: LanguageClient | undefined;
let output: vscode.LogOutputChannel;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  output = vscode.window.createOutputChannel("WPF XAML", { log: true });
  context.subscriptions.push(
    output,
    vscode.commands.registerCommand("wpfXamlLs.restart", () => restart()),
    vscode.commands.registerCommand("wpfXamlLs.showLog", () => output.show()),
    vscode.workspace.onDidChangeConfiguration(async (event) => {
      // Tracing is handled by the client itself; any other setting needs a new server.
      if (event.affectsConfiguration("wpfXamlLs") && !event.affectsConfiguration("wpfXamlLs.trace")) {
        await restart();
      }
    }),
  );

  await start();
}

export async function deactivate(): Promise<void> {
  await client?.stop();
  client = undefined;
}

async function start(): Promise<void> {
  const config = vscode.workspace.getConfiguration("wpfXamlLs");
  const command = config.get<string>("serverPath")?.trim() || "wpf-xaml-ls";

  const serverOptions: ServerOptions = {
    command,
    args: [],
    transport: TransportKind.stdio,
    options: { cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath },
  };

  const clientOptions: LanguageClientOptions = {
    // .xaml files are usually the "xml" language in VS Code; select them by extension whatever the language.
    documentSelector: [{ scheme: "file", pattern: "**/*.xaml" }],
    initializationOptions: {
      solution: config.get<string | null>("solution") ?? undefined,
      loadProjects: config.get<boolean>("loadProjects", true),
      frameworkVersion: config.get<number | null>("frameworkVersion") ?? undefined,
    },
    outputChannel: output,
    traceOutputChannel: output,
  };

  client = new LanguageClient("wpfXamlLs", "WPF XAML", serverOptions, clientOptions);
  try {
    await client.start();
  } catch (error) {
    client = undefined;
    output.error(`Could not start '${command}': ${error}`);
    const choice = await vscode.window.showErrorMessage(
      `WPF XAML: could not start the language server ('${command}'). Install it with \`${installCommand}\`, ` +
        "or set wpfXamlLs.serverPath.",
      "Copy install command",
      "Open settings",
    );
    if (choice === "Copy install command") {
      await vscode.env.clipboard.writeText(installCommand);
    } else if (choice === "Open settings") {
      await vscode.commands.executeCommand("workbench.action.openSettings", "wpfXamlLs");
    }
  }
}

async function restart(): Promise<void> {
  await deactivate();
  await start();
}
