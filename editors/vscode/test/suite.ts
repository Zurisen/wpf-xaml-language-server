// Runs inside VS Code (see runTest.ts): drives the extension through VS Code's own language feature commands.
import * as assert from "assert";
import * as path from "path";
import * as vscode from "vscode";

async function waitFor<T>(what: string, probe: () => Promise<T | undefined> | T | undefined, timeoutMs = 240_000): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const value = await probe();
    if (value !== undefined && value !== null && value !== false) {
      return value;
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`Timed out waiting for ${what}`);
}

function markdown(hovers: vscode.Hover[]): string {
  return hovers.flatMap((h) => h.contents.map((c) => (typeof c === "string" ? c : "value" in c ? c.value : ""))).join("\n");
}

export async function run(): Promise<void> {
  const root = vscode.workspace.workspaceFolders![0].uri.fsPath;
  const file = vscode.Uri.file(path.join(root, "SampleWpfApp", "MainWindow.xaml"));
  const document = await vscode.workspace.openTextDocument(file);
  const editor = await vscode.window.showTextDocument(document);
  const at = (marker: string, delta = 2) => document.positionAt(document.getText().indexOf(marker) + delta);

  // Hover on a project type: proves the server started and loaded the sample projects.
  const hover = await waitFor("project types in hover", async () => {
    const hovers = await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", file, at("BadgeControl"));
    const text = markdown(hovers ?? []);
    return text.includes("SampleWpfApp.BadgeControl") ? text : undefined;
  });
  assert.match(hover, /Shows a small count badge/);
  console.log("PASS hover shows the project type and its docs");

  // Go to definition lands in the C# source.
  const definitions = await vscode.commands.executeCommand<(vscode.Location | vscode.LocationLink)[]>(
    "vscode.executeDefinitionProvider", file, at("BadgeControl"));
  assert.ok(definitions.some((d) => ("uri" in d ? d.uri : d.targetUri).fsPath.endsWith("BadgeControl.cs")), "definition in BadgeControl.cs");
  console.log("PASS go to definition lands in BadgeControl.cs");

  // Binding path completion uses the d:DataContext view model.
  const completions = await vscode.commands.executeCommand<vscode.CompletionList>(
    "vscode.executeCompletionItemProvider", file, at("{Binding Greeting}", "{Binding ".length));
  const labels = completions.items.map((i) => (typeof i.label === "string" ? i.label : i.label.label));
  assert.ok(labels.includes("IsBusy"), `binding completion offers IsBusy (got ${labels.slice(0, 10).join(", ")})`);
  console.log("PASS binding path completion offers IsBusy");

  // A typo gets a diagnostic with a suggestion and a quick fix.
  const start = document.getText().indexOf('FontWeight="Bold"');
  await editor.edit((edit) =>
    edit.replace(new vscode.Range(document.positionAt(start), document.positionAt(start + "FontWeight".length)), "FontWieght"));
  const diagnostic = await waitFor("the typo diagnostic", () =>
    vscode.languages.getDiagnostics(file).find((d) => String(typeof d.code === "object" ? d.code.value : d.code) === "XLS1003"));
  assert.match(diagnostic.message, /Did you mean 'FontWeight'\?/);
  console.log("PASS typo diagnostic with suggestion");

  const actions = await vscode.commands.executeCommand<vscode.CodeAction[]>("vscode.executeCodeActionProvider", file, diagnostic.range);
  assert.ok(actions.some((a) => a.title === "Change to 'FontWeight'"), "quick fix offered");
  console.log("PASS quick fix offered");

  // Leave the sample unchanged.
  await vscode.commands.executeCommand("workbench.action.revertAndCloseActiveEditor");
}
